using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Data.Raster;
using ArcGIS.Core.Data.UtilityNetwork.Trace;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Internal.DesktopService;
using ArcGIS.Desktop.Mapping;
using OpenCvSharp;
using static ArcGISUtils.Utils;
using BitMiracle.LibTiff.Classic;

namespace TornadoSAR
{
    internal class SarAnalyser
    {
        private string prePath;
        private string postPath;
        private string resultsPath;
        private FeatureLayer areaLayer;
        private double bufferWidth;

        private void initalize()
        {
            System.IO.Directory.CreateDirectory(GetProjectPath() + "\\SAR_Analysis");

            resultsPath = GetProjectPath() + "\\SAR_Analysis\\Result_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

            System.IO.Directory.CreateDirectory(resultsPath);
        }

        public SarAnalyser(string prePath, string postPath, FeatureLayer centerlineLayer, double bufferWidth)
        {
            this.prePath = prePath;
            this.postPath = postPath;
            this.areaLayer = centerlineLayer;
            this.bufferWidth = bufferWidth;

            initalize();
        }

        public SarAnalyser(string prePath, string postPath, FeatureLayer polygonLayer)
        {
            this.prePath = prePath;
            this.postPath = postPath;
            this.areaLayer = polygonLayer;

            initalize();
        }

        public async Task<Tuple<List<double>, List<double>>> Analyze()
        {
            Raster collocatedRaster = ContructSARRasters();

            Console.WriteLine("Loading Collocated Raster");
            List<Mat> bands = LoadCollocatedRaster(collocatedRaster);

            Console.WriteLine("Constructing Composite Raster");
            Mat diff = ReverseDifference(bands);

            Console.WriteLine("Loading Composite Raster");
            RasterLayer compositeLayer = LoadComposite(diff);

            Console.WriteLine("Computing AOI Statistics");
            Mat mask = await CenterlineAOIMask(compositeLayer);

            return GetAOIValues(mask, diff);
        }

        public void AnalyzeHail()
        {
            var (postVH, postVV, preVH, preVV, path) = ContructSARHailRasters();

            Console.WriteLine("Constructing Additional bands");
            Mat diffVH = postVH.Subtract(preVH);
            Mat diffVV = postVV.Subtract(preVV);

            Mat preSum = preVH.Add(preVV);
            Mat postSum = postVH.Add(postVV);
            Mat diffSum = postSum.Subtract(preSum);

            //Mat preRVI = preVH.Multiply(4.0).Divide(preSum);
            //Mat postRVI = postVH.Multiply(4.0).Divide(postSum);
            //Mat diffRVI = postRVI.Subtract(preRVI);

            Console.WriteLine("Exporting Additional Bands");
            Mat[] images = [diffVH, diffVV, preSum, postSum, diffSum]; //preRVI, postRVI, diffRVI
            string[] files = ["diffVH.tif", "diffVV.tif", "preSum.tif", "postSum.tif", "diffSum.tif"]; // "preRVI.tif", "postRVI.tif", "diffRVI.tif"

            for (int i = 0; i < images.Length; i++)
            {
                string dstPath = System.IO.Path.Combine(resultsPath, files[i]);
                Tiff tiff = GeoTiff.CreateFromReference(path, dstPath, channels: 1, bitDepth: 32, fp: true);
                GeoTiff.WriteImage<float>(tiff, images[i]);
            }

            Console.WriteLine("Loading Additional Bands");

            for (int i = 0; i < images.Length; i++)
            {
                RasterLayer layer = LoadRasterLayer(resultsPath, files[i]);
                SetSARColorizor(layer);
            }
        }


        private Tuple<List<double>, List<double>> GetAOIValues(Mat mask, Mat diff)
        {
            mask.GetArray(out byte[] maskArray);
            Cv2.Split(diff, out Mat[] diffs);
            diffs[0].GetArray(out float[] VHpixels);
            diffs[1].GetArray(out float[] VVpixels);

            List<double> VHvalues = new(VHpixels.Length);
            List<double> VVvalues = new(VVpixels.Length);

            for (int i = 0; i < maskArray.Length; i++)
            {
                if (maskArray[i] > 0)
                {
                    VHvalues.Add(VHpixels[i]);
                    VVvalues.Add(VVpixels[i]);
                }
            }

            return new (VHvalues, VVvalues);
        }

        private Mat ReverseDifference(List<Mat> bands)
        {
            Mat diffVH = bands[0].Subtract(bands[2]);
            Mat diffVV = bands[1].Subtract(bands[3]);

            Mat diff = new Mat();

            Cv2.Merge([diffVH, diffVV], diff);

            return diff;
        }

        private (double, double) LatLonToMercator(double lat, double lon)
        {

            const double sm_a = 6378137.0;

            lat = lat * Math.PI / 180.0;
            lon = lon * Math.PI / 180.0;

            double x = sm_a * lon;
            double y = sm_a * Math.Log((Math.Sin(lat) + 1) / Math.Cos(lat));

            return (x, y);
        }

        private async Task<Mat> CenterlineAOIMask(RasterLayer refLayer)
        {
            var polyline = ReadPolyline(refLayer, areaLayer);

            var mask = new Mat(new OpenCvSharp.Size(refLayer.GetRaster().GetWidth(), refLayer.GetRaster().GetHeight()), MatType.CV_8UC1, new Scalar(0));

            var latlong = await QueuedTask.Run(() => refLayer.GetRaster().GetMeanCellSize());
            var coords = LatLonToMercator(latlong.Item1, latlong.Item2);

            int scaleX = (int)Math.Round(bufferWidth / coords.Item1);
            int scaleY = (int)Math.Round(bufferWidth / coords.Item2);

            //mask.FillPoly(aoiPolygons, new Scalar(255));
            mask.Polylines([polyline], false, new Scalar(255));
            var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(scaleX, scaleY));

            Cv2.Dilate(mask, mask, kernel);

            return mask;
        }

        private Mat PolygonAOIMask(RasterLayer collocatedLayer)
        {
            var aoiPolygons = ReadPolygons(collocatedLayer, areaLayer);

            var mask = new Mat(new OpenCvSharp.Size(collocatedLayer.GetRaster().GetWidth(), collocatedLayer.GetRaster().GetHeight()), MatType.CV_8UC1, new Scalar(0));
            mask.FillPoly(aoiPolygons, new Scalar(255));

            return mask;
        }

        private string getStandardAOIExtentString()
        {
            Envelope aoiExtent = areaLayer.QueryExtent();
            var extent = (Envelope)GeometryEngine.Instance.Project(aoiExtent, SpatialReferences.WGS84);

            double xmin = RoundDown(extent.XMin, 3);
            double xmax = RoundUp(extent.XMax, 3);
            double ymin = RoundDown(extent.YMin, 3);
            double ymax = RoundUp(extent.YMax, 3);

            return "\"POLYGON((" + xmin + " " + ymax + ", " + xmax + " " + ymax + ", " + xmax + " " + ymin + ", " + xmin + " " + ymin + ", " + xmin + " " + ymax + "))\"";
        }

        private RasterLayer LoadComposite(Mat diff)
        {
            var compositeDataset = OpenRasterDataset(resultsPath, "composite.tif");
            Raster compositeRaster = compositeDataset.CreateFullRaster();

            WriteRaster<float>(diff, compositeRaster);

            var compositeLayer = LoadRasterLayer(resultsPath, "composite.tif");

            CIMRasterRGBColorizer colorizer = new();
            colorizer.RedBandIndex = 2;
            colorizer.GreenBandIndex = 3;
            colorizer.BlueBandIndex = 0;
            colorizer.DisplayBackgroundValue = true;
            compositeLayer.SetColorizer(colorizer);

            MapView.Active.Redraw(true);

            return compositeLayer;
        }

        private List<Mat> LoadCollocatedRaster(Raster collocatedRaster)
        {
            Mat mask = LoadRasterBands<float>(collocatedRaster, [0])[0];
            var postBands = LoadRasterBands<float>(collocatedRaster, [1, 2]);

            //var colorizer = collocatedLayer.GetColorizer() as CIMRasterRGBColorizer;
            //colorizer.RedBandIndex = 3;
            //colorizer.GreenBandIndex = 4;
            //collocatedLayer.SetColorizer(colorizer);

            var preBands = LoadRasterBands<float>(collocatedRaster, [0, 1]);

            //colorizer = collocatedLayer.GetColorizer() as CIMRasterRGBColorizer;
            //colorizer.AlphaBandIndex = 0;
            //colorizer.RedBandIndex = 1;
            //colorizer.GreenBandIndex = 2;
            //colorizer.BlueBandIndex = 3;
            //colorizer.DisplayBackgroundValue = true;

            //collocatedLayer.SetColorizer(colorizer);

            List<Mat> bands = [postBands[0], postBands[1], preBands[0], preBands[1]];

            for (int i = 0; i < bands.Count; i++)
            {
                bands[i] = bands[i].Mul(mask);
            }

            return bands;
        }

        private Raster ContructSARRasters()
        {
            Console.WriteLine("Constructing Pre Raster...");
            string preCorrectedPath = CorrectSARData(prePath, "pre");
            Console.WriteLine("\nConstructing Post Raster...");
            string postCorrectedPath = CorrectSARData(postPath, "post");

            string collocatedPath = "\"" + resultsPath + "\\collocated.dim\"";

            Console.WriteLine("\nConstructing Collocated Raster...");
            SnapWrapper.RunCommand("collocate " + postCorrectedPath + " " + preCorrectedPath + " -t " + collocatedPath);
            SnapWrapper.RunCommand("speckle-filter " + collocatedPath + " -Pfilter=\"Refined Lee\" -PsourceBands=\"Sigma0_VH_M, Sigma0_VV_M, Sigma0_VH_S, Sigma0_VV_S\" -f Geotiff -t \"" + resultsPath + "\\collocated.tif\"");
            SnapWrapper.RunCommand("speckle-filter " + collocatedPath + " -Pfilter=\"Refined Lee\" -PsourceBands=\"Sigma0_VH_S, Sigma0_VH_M, Sigma0_VV_M\" -f Geotiff -t \"" + resultsPath + "\\composite.tif\"", verbose: false);

            Raster collocatedRaster = OpenRasterDataset(resultsPath, "collocated.tif").CreateFullRaster();

            return collocatedRaster;
        }

        private (Mat, Mat, Mat, Mat, string) ContructSARHailRasters()
        {
            Console.WriteLine("Constructing Pre Raster...");
            string preCorrectedPath = CorrectSARData(prePath, "pre");
            Console.WriteLine("\nConstructing Post Raster...");
            string postCorrectedPath = CorrectSARData(postPath, "post");

            string collocatedPath = "\"" + resultsPath + "\\collocated.dim\"";

            Console.WriteLine("\nConstructing Collocated Raster...");
            SnapWrapper.RunCommand("collocate " + postCorrectedPath + " " + preCorrectedPath + " -t " + collocatedPath);

            Console.WriteLine("\nExporting Individual Pre and Post Bands...");
            SnapWrapper.RunCommand("subset " + collocatedPath + " -PsourceBands=\"Sigma0_VH_M\" -t \"" + resultsPath + "\\postVH.dim\"");
            SnapWrapper.RunCommand("subset " + collocatedPath + " -PsourceBands=\"Sigma0_VV_M\" -t \"" + resultsPath + "\\postVV.dim\"");
            SnapWrapper.RunCommand("subset " + collocatedPath + " -PsourceBands=\"Sigma0_VH_S\" -t \"" + resultsPath + "\\preVH.dim\"");
            SnapWrapper.RunCommand("subset " + collocatedPath + " -PsourceBands=\"Sigma0_VV_S\" -t \"" + resultsPath + "\\preVV.dim\"");

            Console.WriteLine("\nDenoising Images...");
            SnapWrapper.RunCommand("speckle-filter \"" + resultsPath + "\\postVH.dim\"" + " -Pfilter=\"Lee\" -f Geotiff -t \"" + resultsPath + "\\postVH.tif\"");
            SnapWrapper.RunCommand("speckle-filter \"" + resultsPath + "\\postVV.dim\"" + " -Pfilter=\"Lee\" -f Geotiff -t \"" + resultsPath + "\\postVV.tif\"");
            SnapWrapper.RunCommand("speckle-filter \"" + resultsPath + "\\preVH.dim\"" + " -Pfilter=\"Lee\" -f Geotiff -t \"" + resultsPath + "\\preVH.tif\"");
            SnapWrapper.RunCommand("speckle-filter \"" + resultsPath + "\\preVV.dim\"" + " -Pfilter=\"Lee\" -f Geotiff -t \"" + resultsPath + "\\preVV.tif\"");

            Console.WriteLine("\nRemoving Temporary Files...");
            EmptyFolder(resultsPath, ".tif");

            Console.WriteLine("\nLoading Pre and Post Rasters...");
            RasterLayer postVHLayer = LoadRasterLayer(resultsPath, "postVH.tif");
            RasterLayer postVVLayer = LoadRasterLayer(resultsPath, "postVV.tif");
            RasterLayer preVHLayer = LoadRasterLayer(resultsPath, "preVH.tif");
            RasterLayer preVVLayer = LoadRasterLayer(resultsPath, "preVV.tif");

            SetSARColorizor(postVHLayer);
            SetSARColorizor(postVVLayer);
            SetSARColorizor(preVHLayer);
            SetSARColorizor(preVVLayer);

            Mat postVH = LoadRasterImage<float>(postVHLayer.GetRaster());
            Mat postVV = LoadRasterImage<float>(postVVLayer.GetRaster());
            Mat preVH = LoadRasterImage<float>(preVHLayer.GetRaster());
            Mat preVV = LoadRasterImage<float>(preVVLayer.GetRaster());

            return (postVH, postVV, preVH, preVV, resultsPath + "\\postVH.tif");
        }

        private void SetSARColorizor(RasterLayer layer)
        {
            var colorizor = layer.GetColorizer() as CIMRasterStretchColorizer;
            colorizor.StretchType = RasterStretchType.StandardDeviations;
            colorizor.StandardDeviationParam = 2;
            colorizor.DisplayBackgroundValue = true;
            layer.SetColorizer(colorizor);
        }

        private string CorrectSARData(string sarPath, string name)
        {
            string aoiExtentString = getStandardAOIExtentString();

            string targetPath = "\"" + resultsPath + "\\" + name;

            SnapWrapper.RunCommand("calibration \"" + sarPath + "\" -t " + targetPath + "_calibration.dim\"");

            SnapWrapper.RunCommand("subset " + targetPath + "_calibration.dim\"" + " -PgeoRegion=" + aoiExtentString + " -t " + targetPath + "_subset.dim\"");

            SnapWrapper.RunCommand("terrain-correction " + targetPath + "_subset.dim\"" + " -t " + targetPath + ".dim\"");

            return targetPath + ".dim\"";
        }

        private Mat PercentWindowNormalize(Mat im, double p)
        {
            int length = im.Rows * im.Cols;
            float[] imageArray = new float[length];
            im.GetArray(out imageArray);

            Array.Sort(imageArray);
            float min = imageArray[(int)(length * p)];
            float max = imageArray[(int)(length * (1.0 - p))];

            Mat nim = im.Clone();

            ForeachPixel(nim, (i, j) =>
            {
                float value = Math.Min(Math.Max(im.At<float>(i, j) - min, 0.0f) / (max - min), 1.0f);
                nim.Set(i, j, value);
            });

            return nim;
        }

        private Mat Normalized_VH_VV_Difference(List<Mat> bands)
        {
            Mat diffVH = bands[0].Subtract(bands[2]);
            Mat diffVV = bands[1].Subtract(bands[3]);

            diffVH = PercentWindowNormalize(diffVH, 0.05);
            diffVV = PercentWindowNormalize(diffVV, 0.05);

            Mat diffMul = diffVH.Mul(diffVV);

            return diffMul;
        }

    }
}
