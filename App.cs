using Autodesk.Revit.UI;
using System;
using System.Reflection;
using System.Windows.Media.Imaging;
using System.IO;
//using System.Windows.Resources;
//using System.Windows.Shapes;


namespace Brand_25
{
    public class App : IExternalApplication
    {
        // Exposed so Text_HideTemp / Text_UnhideTemp can flip which icon
        // is shown on the split button after they run.
        public static SplitButton TextSplitButton { get; private set; }
        public static PushButton HideMarkupsButton { get; private set; }
        public static PushButton UnhideMarkupsButton { get; private set; }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }

        public Result OnStartup(UIControlledApplication application)
        {
            //string assembName = Assembly.GetExecutingAssembly().Location;
            //string path = System.IO.Path.GetDirectoryName(assembName);

            string tabName = "Brand";
            application.CreateRibbonTab(tabName);


            RibbonPanel panel1 = application.CreateRibbonPanel(tabName, "Grid");
            PushButtonData btnData1a = new PushButtonData("btn1a", "Propagate\nGrid Status", Assembly.GetExecutingAssembly().Location, "Brand_25.Grid_PropagateStatus");
            PushButtonData btnData1b = new PushButtonData("btn1b", "Togggle Grid\nBubbles", Assembly.GetExecutingAssembly().Location, "Brand_25.Grid_BubbleToggle");
            PushButtonData btnData1c = new PushButtonData("btn1c", "Trim/Extend\nGrids", Assembly.GetExecutingAssembly().Location, "Brand_25.Grid_TrimGrid");
            btnData1a.LargeImage = LoadEmbeddedImage("Brand_25.Resources.Images.Grid_Propagate_Status_32.png");
            btnData1b.LargeImage = LoadEmbeddedImage("Brand_25.Resources.Images.Grid_Toggle_Bubble_32.png");
            btnData1c.LargeImage = LoadEmbeddedImage("Brand_25.Resources.Images.Grid_Trim_Extend_32.png");
            btnData1a.ToolTip = "Copy the status of the grids (gridline visibility, gridline extent and bubble visibility) from active view to views selected.\nNote: There is a built-in function in Revit called 'Propagate Extents' in the contextual menu of grids";
            btnData1b.ToolTip = "Toggle Grid Bubble by user. A detail line as fence line is needed.";
            btnData1c.ToolTip = "Trim or Extend grid lines to a detail line created by user.";
            //btnData1a.ToolTipImage = new BitmapImage(new Uri(path + "\\grid_512.png"));
            panel1.AddItem(btnData1a);
            panel1.AddItem(btnData1b);
            panel1.AddItem(btnData1c);


            RibbonPanel panel2 = application.CreateRibbonPanel(tabName, "Curtain Wall");
            PushButtonData btnData2a = new PushButtonData("btn2a", "Disallow Join\nCurtain Walls", Assembly.GetExecutingAssembly().Location, "Brand_25.CW_DisallowJoin");
            btnData2a.LargeImage = LoadEmbeddedImage("Brand_25.Resources.Images.Wall_Joint_Disallow_32.png");
            btnData2a.ToolTip = "Change the wall end conditions of selected Curtain Wall types to 'Disallow Join'";
            panel2.AddItem(btnData2a);


            RibbonPanel panel3 = application.CreateRibbonPanel(tabName, "Room");
            PushButtonData btnData3a = new PushButtonData("btn3a", "Re-center\nRoom Point", Assembly.GetExecutingAssembly().Location, "Brand_25.Room_Recentre");
            PushButtonData btnData3b = new PushButtonData("btn3b", "Create\nInternal Elev", Assembly.GetExecutingAssembly().Location, "Brand_25.Room_CreateIntElev");
            PushButtonData btnData3c = new PushButtonData("btn3c", "Place Elev\non Sheet", Assembly.GetExecutingAssembly().Location, "Brand_25.Elev_PlaceOnSheets");
            btnData3a.LargeImage = LoadEmbeddedImage("Brand_25.Resources.Images.Room_Re-center_32.png");
            btnData3b.LargeImage = LoadEmbeddedImage("Brand_25.Resources.Images.intelev_32.png");
            btnData3c.LargeImage = LoadEmbeddedImage("Brand_25.Resources.Images.elev_on_sheet_32.png"); // swap for a real icon
            btnData3a.ToolTip = "Relocate the Room insertion point to the centre of the room";
            btnData3b.ToolTip = "Create internal elevations for selected rooms";
            btnData3c.ToolTip = "Place internal elevation views onto a sheet in a row-packed layout, creating additional sheets as needed.";
            panel3.AddItem(btnData3a);
            panel3.AddItem(btnData3b);
            panel3.AddItem(btnData3c);


            RibbonPanel panel4 = application.CreateRibbonPanel(tabName, "Text");
            PushButtonData btnData4a = new PushButtonData("btn4a", "Hide\nMark-ups", Assembly.GetExecutingAssembly().Location, "Brand_25.Text_HideTemp");
            PushButtonData btnData4b = new PushButtonData("btn4b", "UnHide\nMark-ups", Assembly.GetExecutingAssembly().Location, "Brand_25.Text_UnhideTemp");
            //PushButtonData btnData4c = new PushButtonData("btn4c", "Consolidate\nTypes", Assembly.GetExecutingAssembly().Location, "Brand_25.Text_ConsolidateTypes");
            btnData4a.LargeImage = LoadEmbeddedImage("Brand_25.Resources.Images.hide_markup_32.png");
            btnData4b.LargeImage = LoadEmbeddedImage("Brand_25.Resources.Images.unhide_markup_32.png");
            //btnData4c.LargeImage = LoadEmbeddedImage("Brand_25.Resources.Images.Placeholder_32.png");
            btnData4a.ToolTip = "Hide the Markup and Temp Text by keyword in Text Type";
            btnData4b.ToolTip = "Unhide the hidden text. You must have a hidden text to proceed with.";
            //btnData4c.ToolTip = "Find TextNote Types with identical attributes and consolidate duplicates into a single type.";
            //panel4.AddItem(btnData4c);

            RibbonPanel panel5 = application.CreateRibbonPanel(tabName, "Material");
            PushButtonData btnData5a = new PushButtonData("btn5a", "Duplicate\nKeynotes", Assembly.GetExecutingAssembly().Location, "Brand_25.Mat_FindDupKeynote");
            btnData5a.LargeImage = LoadEmbeddedImage("Brand_25.Resources.Images.Placeholder_32.png"); // swap for a real icon
            btnData5a.ToolTip = "Find all materials that share the same Keynote value and display them in a grouped report.";
            panel5.AddItem(btnData5a);

            // Control-test panel: standalone, unrelated to the main workflow. Remove once
            // the GetCropShape/SetCropShape investigation is finished.
            RibbonPanel panelTest = application.CreateRibbonPanel(tabName, "Test");
            //PushButtonData btnDataTestCrop = new PushButtonData("btnTestCrop", "Test Crop\nShape Adjust", Assembly.GetExecutingAssembly().Location, "Brand_25.Elev_TestCropShapeAdjust");
            PushButtonData btnDataTestc = new PushButtonData("btn4c", "Consolidate\nTypes", Assembly.GetExecutingAssembly().Location, "Brand_25.Text_ConsolidateTypes");
            //btnDataTestCrop.LargeImage = LoadEmbeddedImage("Brand_25.Resources.Images.Placeholder_32.png"); // swap for a real icon
            btnDataTestc.LargeImage = LoadEmbeddedImage("Brand_25.Resources.Images.Placeholder_32.png");
            //btnDataTestCrop.ToolTip = "CONTROL TEST: lowers the active elevation/section view's crop shape top by 2 feet via GetCropShape/SetCropShape, done as a standalone action on an already-existing view. Reports success or the exact exception.";
            btnDataTestc.ToolTip = "Find TextNote Types with identical attributes and consolidate duplicates into a single type.";
            //panelTest.AddItem(btnDataTestCrop);
            panelTest.AddItem(btnDataTestc);

            SplitButtonData sb1 = new SplitButtonData("splitButton1", "split");
            SplitButton sb = panel4.AddItem(sb1) as SplitButton;
            PushButton pbHide = sb.AddPushButton(btnData4a);
            PushButton pbUnhide = sb.AddPushButton(btnData4b);

            // CurrentButton can only be set while IsSynchronizedWithCurrentItem stays true
            // (Revit throws InvalidOperationException otherwise). Leaving it at its default
            // (true) still lets us override the icon manually after each command runs —
            // our assignment in Text_HideTemp/Text_UnhideTemp happens after Revit's own
            // auto-sync-on-click, so it takes precedence.
            sb.CurrentButton = pbHide; // nothing hidden yet at startup

            TextSplitButton = sb;
            HideMarkupsButton = pbHide;
            UnhideMarkupsButton = pbUnhide;


            //RibbonPanel panelP = application.CreateRibbonPanel(tabName, "Placeholder");
            //PushButtonData btnDataP = new PushButtonData("btnP", "Placeholder", Assembly.GetExecutingAssembly().Location, "Brand_25.XxTrial");
            //btnDataP.LargeImage = LoadEmbeddedImage("Brand_25.Resources.Images.Placeholder_32.png");
            //btnDataP.ToolTip = "Placeholder";
            //panelP.AddItem(btnDataP);



            //btnData6.LargeImage = new BitmapImage(new Uri(path + "\\placeholder_32.png"));
            //btnData2a.Image = new BitmapImage(new Uri(path + "\\placeholder_16.png"));
            //btnData3a.Image = new BitmapImage(new Uri(path + "\\placeholder_16.png"));
            //PushButtonData btnData6 = new PushButtonData("btn6", "Placeholder6", assembName, "Brand_25.ShowDialog");

            //panel1.AddSeparator();
            //panel1.AddStackedItems(btnData2a, btnData3a);


            return Result.Succeeded;
        }
        private BitmapImage LoadEmbeddedImage(string resourceName)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null) throw new Exception($"Embedded resource not found: {resourceName}");

                BitmapImage image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = stream;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                return image;
            }
        }
    }
}
