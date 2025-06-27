using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System;
using System.Threading.Tasks;
using System.Windows;
using ArcGIS.Core.Geometry;
using static ArcGISUtils.Utils;

namespace TornadoSAR
{
    public partial class HailAnalysis : ArcGIS.Desktop.Framework.Controls.ProWindow
    {
        private TextBoxStreamWriter textBoxstreamWriter;

        public HailAnalysis()
        {
            InitializeComponent();

            PreLoadDlls();
        }

        private async void RunAnalysis(object sender, RoutedEventArgs e)
        {
            try
            {
                if (await MissingInput()) return;

                runButton.IsEnabled = false;

                tabController.SelectedIndex = 1;

                textBoxstreamWriter = new TextBoxStreamWriter(ConsoleTextBox);
                textBoxstreamWriter.RedirectStandardOutput();

                SarAnalyser sarAnalyser = BuildSarAnalyser();

                await QueuedTask.Run(() =>
                {
                    PrintTitle();

                    sarAnalyser.AnalyzeHail();

                    Console.WriteLine("Done.\n");
                });

                textBoxstreamWriter.StopSpinning();

            }
            catch (Exception ex)
            {
                MessageBox.Show("Something went wrong... :(\n\nError:\n" + ex.ToString());
            }

            runButton.IsEnabled = true;
        }

        private SarAnalyser BuildSarAnalyser()
        {
            return new SarAnalyser(preEventSelection.GetFilePath(), postEventSelection.GetFilePath(), polygonSelection.GetSelectedLayer());
        }

        private async Task<bool> MissingInput()
        {
            if (preEventSelection.IsEmpty())
            {
                MessageBox.Show("Please select a pre-event zip file");
                return true;
            }
            if (postEventSelection.IsEmpty())
            {
                MessageBox.Show("Please select a post-event zip file");
                return true;
            }
            if (polygonSelection.IsEmpty())
            {
                MessageBox.Show("Please select a centerline shapefile");
                return true;
            }

            FeatureLayer polygonLayer = polygonSelection.GetSelectedLayer();

            return await QueuedTask.Run(() =>
            {
                if (polygonLayer.GetSpatialReference().Name.ToLower().Contains("unkown"))
                {
                    MessageBox.Show("Shapefile does not have a spatial reference. Please use the Define Projection tool");
                    return true;
                }

                if (IsShapeFileOfType<Polygon>(polygonLayer)) return false;

                MessageBox.Show("Please select a polyline shapefile");
                return true;
            });
        }

        private void PrintTitle()
        {
            Console.Write(" ______                      __       _______   ___ \n" +
                          "/_  __/__  _______  ___ ____/ /__    / __/ _ | / _ \\\n" +
                          " / / / _ \\/ __/ _ \\/ _ `/ _  / _ \\  _\\ \\/ __ |/ , _/\n" +
                          "/_/  \\___/_/ /_//_/\\_,_/\\_,_/\\___/ /___/_/ |_/_/|_| \n\n" +
                          "----------------------------------------------------------\n\n");
        }
    }
}
