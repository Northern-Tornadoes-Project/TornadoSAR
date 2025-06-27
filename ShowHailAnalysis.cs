using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Extensions;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Dialogs;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.KnowledgeGraph;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TornadoSAR
{
    internal class ShowHailAnalysis : Button
    {

        private HailAnalysis _hailanalysis = null;

        protected override void OnClick()
        {
            //already open?
            if (_hailanalysis != null)
                return;
            _hailanalysis = new HailAnalysis();
            _hailanalysis.Owner = FrameworkApplication.Current.MainWindow;
            _hailanalysis.Closed += (o, e) => { _hailanalysis = null; };
            _hailanalysis.Show();
            //uncomment for modal
            //_hailanalysis.ShowDialog();
        }

    }
}
