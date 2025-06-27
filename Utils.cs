using ArcGIS.Core.Data;
using ArcGIS.Core.Data.Raster;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Mapping;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ArcGISUtils
{
    internal class Utils
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadLibrary(string dllToLoad);

        private static bool LoadDll(string path)
        {
            IntPtr hModule = LoadLibrary(path);

            if (hModule == IntPtr.Zero)
            {
                int errorCode = Marshal.GetLastWin32Error();
                System.Windows.MessageBox.Show("Failed to load DLLs: " + errorCode.ToString());
                return false;
            }

            return true;
        }

        public static bool PreLoadDlls()
        {
            string path;

            #if DEBUG
                path = @"C:\Users\danie\Documents\Experiments\SAR analysis\TornadoSAR\bin\Debug\net8.0-windows";
            #else
                path = AddinAssemblyLocation();
            #endif

            string[] dlls = ["OpenCvSharp", "OpenCvSharpExtern"];

            foreach (string dll in dlls)
            {
                if (!LoadDll(path + "\\" + dll + ".dll")) return false;
            }

            return true;
        }

        public static string AddinAssemblyLocation()
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();

            return System.IO.Path.GetDirectoryName(Uri.UnescapeDataString(new Uri(asm.Location).LocalPath));
        }

        public static string GetProjectPath()
        {
            return System.IO.Path.GetDirectoryName(ArcGIS.Desktop.Core.Project.Current.URI);
        }

        public static RasterLayer LoadRasterLayer(string directory, string name)
        {
            var rasterDataset = OpenRasterDataset(directory, name);
            var rasterLayerCreationParams = new RasterLayerCreationParams(rasterDataset);
            
            return LayerFactory.Instance.CreateLayer<RasterLayer>(rasterLayerCreationParams, MapView.Active.Map);

            //return LayerFactory.Instance.CreateLayer(new Uri(path), MapView.Active.Map) as RasterLayer;
        }

        public static Tuple<int, int, int, int> EnvolopeToImageRect(Envelope envelope, Raster raster)
        {
            var topLeft = raster.MapToPixel(envelope.XMin, envelope.YMax);
            var bottomRight = raster.MapToPixel(envelope.XMax, envelope.YMin);

            int x = Math.Min(topLeft.Item1, bottomRight.Item1);
            int y = Math.Min(topLeft.Item2, bottomRight.Item2);
            int width = Math.Abs(bottomRight.Item1 - topLeft.Item1);
            int height = Math.Abs(topLeft.Item2 - bottomRight.Item2);

            return new Tuple<int, int, int, int>(x, y, width, height);
        }

        public static Mat LoadRasterImage<T>(Raster raster, Tuple<int, int, int, int> rect=null) where T : unmanaged
        {
            int bandCount = Math.Min(raster.GetBandCount(), 4);

            var (x, y, width, height) = rect ?? new(0, 0, raster.GetWidth(), raster.GetHeight());

            var pixelBlock = raster.CreatePixelBlock(width, height);
            raster.Read(x, y, pixelBlock);

            Mat[] bands = new Mat[bandCount];

            for (int b = 0; b < bandCount; b++)
            {
                bands[b] = new Mat(height, width, UnmangedToMatType<T>());
                var rasterData = (T[,])pixelBlock.GetPixelData(b, false);

                ForeachPixel(bands[b], (i, j) =>
                {
                    bands[b].Set(i, j, rasterData[j, i]);
                });
            }

            Mat im = new();
            Cv2.Merge(bands, im);

            return im;
        }

        public static List<Mat> LoadRasterBands<T>(Raster raster, int[] bandIndices) where T : unmanaged
        {
            var pixelBlock = raster.CreatePixelBlock(raster.GetWidth(), raster.GetHeight());
            raster.Read(0, 0, pixelBlock);

            int planeCount = pixelBlock.GetPlaneCount();

            List<Mat> bands = new(bandIndices.Length);

            for (int b = 0; b < bandIndices.Length; b++)
            {
                bands.Add(new Mat(raster.GetHeight(), raster.GetWidth(), UnmangedToMatType<T>()));
                var rasterData = (T[,])pixelBlock.GetPixelData(bandIndices[b], false);

                ForeachPixel(bands[b], (i, j) =>
                {
                    bands[b].Set(i, j, rasterData[j, i]);
                });
            }

            return bands;
        }

        public static MatType UnmangedToMatType<T>() where T : unmanaged
        {
            if (typeof(T) == typeof(float))
            {
                return MatType.CV_32FC1;
            }
            else if (typeof(T) == typeof(double))
            {
                return MatType.CV_64FC1;
            }
            else if (typeof(T) == typeof(int))
            {
                return MatType.CV_32S;
            }

            return MatType.CV_8UC1;
        }
            

        // Parallel for
        public static void ForeachPixel(Mat im, Action<int, int> op)
        {
            Parallel.For(0, im.Height, i =>
            {
                for (int j = 0; j < im.Width; j++)
                {
                    op(i, j);
                }
            });
        }

        public static void WriteRaster<T>(Mat im, Raster raster) where T : unmanaged
        {
            var pixelBlock = raster.CreatePixelBlock(raster.GetWidth(), raster.GetHeight());
            raster.Read(0, 0, pixelBlock);

            for (int b = 0; b < im.Channels(); b++)
            {
                Mat band = im.ExtractChannel(b);
                var rasterData = (T[,])pixelBlock.GetPixelData(b, false);

                for (int i = 0; i < raster.GetHeight(); i++)
                {
                    for (int j = 0; j < raster.GetWidth(); j++)
                    {
                        rasterData[j, i] = band.At<T>(i, j);
                    }
                }

                pixelBlock.SetPixelData(b, rasterData);
            }

            raster.Write(0, 0, pixelBlock);
            raster.Refresh();

            pixelBlock.Dispose();
        }

        public static RasterDataset OpenRasterDataset(string folder, string name)
        {
            RasterDataset rasterDataset = null;

            try
            {
                FileSystemConnectionPath connectionPath = new(new System.Uri(folder), FileSystemDatastoreType.Raster);

                FileSystemDatastore dataStore = new(connectionPath);

                rasterDataset = dataStore.OpenDataset<RasterDataset>(name);

                if (rasterDataset == null) System.Windows.MessageBox.Show("Failed to open raster dataset: " + name);
            }
            catch (Exception exc)
            {
                System.Windows.MessageBox.Show("Exception caught in OpenRasterDataset for raster: " + name + exc.Message);
            }

            return rasterDataset;
        }

        public static bool IsShapeFile(FeatureLayer layer)
        {
            using FeatureClass featureClass = layer.GetFeatureClass();
            using Datastore datastore = featureClass.GetDatastore(); //core.data.geodatabase

            return datastore is FileSystemDatastore || datastore is Geodatabase;
        }

        public static bool IsShapeFileOfType<T>(FeatureLayer layer) where T : Geometry
        {
            if (!IsShapeFile(layer)) return false;

            string layertype = layer.ShapeType.ToString().Substring(12).ToLower();
            var splitShapeType = typeof(T).ToString().Split(".");
            string shapetype = splitShapeType[splitShapeType.Length - 1].ToLower();

            if (layertype != shapetype) return false;

            return true;
        }
        

        public static List<T> ReadShapes<T>(FeatureLayer layer) where T : Geometry
        {
            List<T> list = new List<T>();
            using RowCursor rowCursor = layer.GetTable().Search();

            while (rowCursor.MoveNext())
            {
                Geometry shape = ((Feature)rowCursor.Current).GetShape();
                list.Add((T)shape);
            }

            return list;
        }

        public static List<List<Point>> ReadPolygons(RasterLayer rasterLayer, FeatureLayer polygonLayer)
        {
            List<List<Point>> polygons = [];

            Raster raster = rasterLayer.GetRaster();

            using Table shp_table = polygonLayer.GetTable();
            using RowCursor rowCursor = shp_table.Search();
            while (rowCursor.MoveNext())
            {
                using Feature f = (Feature)rowCursor.Current;
                Polygon polyShape = (Polygon)GeometryEngine.Instance.Project(f.GetShape(), rasterLayer.GetSpatialReference());
                List<Point> polygon = [];

                foreach (var p in polyShape.Points)
                {
                    var pixelP = raster.MapToPixel(p.X, p.Y);
                    polygon.Add(new Point(pixelP.Item1, pixelP.Item2));
                }

                polygons.Add(polygon);
            }

            return polygons;
        }

        public static List<Point> ReadPolyline(RasterLayer rasterLayer, FeatureLayer polylineLayer)
        {
            List<Point> polyline = [];

            Raster raster = rasterLayer.GetRaster();

            using Table shp_table = polylineLayer.GetTable();
            using RowCursor rowCursor = shp_table.Search();

            rowCursor.MoveNext();

            using Feature f = (Feature)rowCursor.Current;
            Polyline polyShape = (Polyline)GeometryEngine.Instance.Project(f.GetShape(), rasterLayer.GetSpatialReference());
            List<Point> polygon = [];

            foreach (var p in polyShape.Points)
            {
                var pixelP = raster.MapToPixel(p.X, p.Y);
                polyline.Add(new Point(pixelP.Item1, pixelP.Item2));
            }

            return polyline;
        }

        public static void EmptyFolder(string folderPath, string extToKeep=null)
        {
            if (extToKeep == null)
            {
                Directory.Delete(folderPath, recursive: true);
                return;
            }

            var directoryInfo = new DirectoryInfo(folderPath);

            foreach (FileInfo file in directoryInfo.GetFiles())
            {
                if (file.Extension.Equals(extToKeep, StringComparison.CurrentCultureIgnoreCase)) continue;
                file.Delete();
            }

            foreach (DirectoryInfo subfolder in directoryInfo.GetDirectories())
            {
                subfolder.Delete(recursive: true);
            }
        }

        public static double RoundUp(double value, int decimalPlaces)
        {
            double multiplier = Math.Pow(10, decimalPlaces);
            return Math.Ceiling(value * multiplier) / multiplier;
        }

        public static double RoundDown(double value, int decimalPlaces)
        {
            double multiplier = Math.Pow(10, decimalPlaces);
            return Math.Floor(value * multiplier) / multiplier;
        }

    }
}
