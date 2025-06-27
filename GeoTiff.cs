using ArcGIS.Desktop.Internal.Mapping;
using BitMiracle.LibTiff.Classic;
using OpenCvSharp;
using System;
using System.Configuration.Internal;
using System.IO;
using System.Security.Cryptography;

namespace TornadoSAR
{
    internal static class GeoTiff
    {
        static GeoTiff()
        {
            Tiff.SetErrorHandler(new DisableErrorHandler());
        }

        private class DisableErrorHandler : TiffErrorHandler
        {
            public override void WarningHandler(Tiff tif, string method, string format, params object[] args)
            {
                // do nothing, ie, do not write warnings to console
            }
            public override void WarningHandlerExt(Tiff tif, object clientData, string method, string format, params object[] args)
            {
                // do nothing ie, do not write warnings to console
            }
        }

        private static void RegisterGeoTiffTags(Tiff tif)
        {
            // LibTiff defines FIELD_CUSTOM = 65, but any unused bit ≥ 64 works.
            const short FIELD_CUSTOM = 65;
            const bool CHANGE_OK = false;   // we never touch these tags after writing
            const bool PASS_COUNT = true;    // libtiff passes the count to SetField

            TiffFieldInfo[] geo =
            {
                new ((TiffTag)33550, -1, -1, TiffType.DOUBLE, FIELD_CUSTOM, CHANGE_OK, PASS_COUNT, "ModelPixelScaleTag"),

                new ((TiffTag)33922, -1, -1, TiffType.DOUBLE, FIELD_CUSTOM, CHANGE_OK, PASS_COUNT, "ModelTiepointTag"),

                new ((TiffTag)34264, -1, -1, TiffType.DOUBLE, FIELD_CUSTOM, CHANGE_OK, PASS_COUNT, "ModelTransformationTag"),

                new ((TiffTag)34735, -1, -1, TiffType.SHORT, FIELD_CUSTOM, CHANGE_OK, PASS_COUNT, "GeoKeyDirectoryTag"),

                new((TiffTag)34736, -1, -1, TiffType.DOUBLE, FIELD_CUSTOM, CHANGE_OK, PASS_COUNT, "GeoDoubleParamsTag"),

                new ((TiffTag)34737, -1, -1, TiffType.ASCII, FIELD_CUSTOM, CHANGE_OK, PASS_COUNT, "GeoAsciiParamsTag"),
            };

            tif.MergeFieldInfo(geo, geo.Length);
        }

        private static void CopyGeoTags(Tiff src, Tiff dst)
        {
            RegisterGeoTiffTags(src);
            RegisterGeoTiffTags(dst);

            FieldValue[] fv1 = src.GetField((TiffTag)33550);
            FieldValue[] fv2 = src.GetField((TiffTag)33922);
            FieldValue[] fv3 = src.GetField((TiffTag)34264);
            FieldValue[] fv4 = src.GetField((TiffTag)34736);
            FieldValue[] fv5 = src.GetField((TiffTag)34735);
            FieldValue[] fv6 = src.GetField((TiffTag)34737);

            if (!fv1.IsNullOrEmpty()) dst.SetField((TiffTag)33550, fv1[0].ToInt(), fv1[1].ToDoubleArray());
            if (!fv2.IsNullOrEmpty()) dst.SetField((TiffTag)33922, fv2[0].ToInt(), fv2[1].ToDoubleArray());
            if (!fv3.IsNullOrEmpty()) dst.SetField((TiffTag)34264, fv3[0].ToInt(), fv3[1].ToDoubleArray());
            if (!fv4.IsNullOrEmpty()) dst.SetField((TiffTag)34736, fv4[0].ToInt(), fv4[1].ToDoubleArray());
            if (!fv5.IsNullOrEmpty()) dst.SetField((TiffTag)34735, fv5[0].ToInt(), fv5[1].ToShortArray());
            if (!fv6.IsNullOrEmpty()) dst.SetField((TiffTag)34737, fv6[0].ToInt(), fv6[1].ToString());
        }

        public static Tiff CreateFromReference(byte[] imageBytes, string dstPath, int channels = 1, int bitDepth = 8, bool fp = false)
        {
            using MemoryStream ms = new(imageBytes);
            TiffStream tiffStream = new();
            using Tiff src = Tiff.ClientOpen("in-memory", "r", ms, tiffStream);

            int width = src.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
            int height = src.GetField(TiffTag.IMAGELENGTH)[0].ToInt();

            var dst = Tiff.Open(dstPath, "w");

            dst.SetField(TiffTag.IMAGEWIDTH, width);
            dst.SetField(TiffTag.IMAGELENGTH, height);
            dst.SetField(TiffTag.SAMPLESPERPIXEL, channels);
            dst.SetField(TiffTag.BITSPERSAMPLE, bitDepth);
            dst.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);
            dst.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
            dst.SetField(TiffTag.COMPRESSION, Compression.LZW);

            if (fp) dst.SetField(TiffTag.SAMPLEFORMAT, SampleFormat.IEEEFP);

            CopyGeoTags(src, dst);

            return dst;
        }

        public static Tiff CreateFromReference(string srcPath, string dstPath, int channels = 1, int bitDepth = 8, bool fp = false)
        {
            using var src = Tiff.Open(srcPath, "r");
            if (src == null) throw new Exception("Cannot open TIFF");

            int width = src.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
            int height = src.GetField(TiffTag.IMAGELENGTH)[0].ToInt();

            var dst = Tiff.Open(dstPath, "w");

            dst.SetField(TiffTag.IMAGEWIDTH, width);
            dst.SetField(TiffTag.IMAGELENGTH, height);
            dst.SetField(TiffTag.SAMPLESPERPIXEL, channels);
            dst.SetField(TiffTag.BITSPERSAMPLE, bitDepth);
            dst.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);
            dst.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
            dst.SetField(TiffTag.COMPRESSION, Compression.LZW);

            if (fp)
            {
                dst.SetField(TiffTag.SAMPLEFORMAT, SampleFormat.IEEEFP);
                dst.SetField(TiffTag.PREDICTOR, 3); //better compression for floating point data
            }

            CopyGeoTags(src, dst);

            return dst;
        }

        public static void WriteImage<T>(Tiff dst, Mat image) where T : unmanaged
        {
            unsafe
            {
                int size = image.Width * image.Channels() * sizeof(T);
                byte[] rowBuf = new byte[size];

                for (int y = 0; y < image.Height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(image.Ptr(y), rowBuf, 0, size);

                    if (!dst.WriteScanline(rowBuf, y)) throw new ApplicationException($"Failed writing row {y}");
                }
            }

            //dst.WriteDirectory();
            dst.Close();
        }
    }
}
