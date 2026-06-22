using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Palisades.Models
{
    public class PluginGadgetItem : INotifyPropertyChanged
    {
        private Guid _id = Guid.NewGuid();
        private string _pluginId = "";
        private string _gadgetType = "";
        private string _title = "Gadget";
        private double _x = 150;
        private double _y = 150;
        private double _width = 250;
        private double _height = 180;
        private string _customData = "";
        private bool _hideHeader;
        private double _opacity = 1.0;
        private double _marginLeft;
        private double _marginTop;
        private double _marginRight;
        private double _marginBottom;
        private double _paddingLeft;
        private double _paddingTop;
        private double _paddingRight;
        private double _paddingBottom;

        private string _bgColor = "#15000000";
        private double _bgOpacity = 0.15;
        private string _borderColor = "#35FFFFFF";
        private double _borderThicknessValue = 1.5;
        private double _cornerRadiusValue = 8.0;
        private string _headerBgColor = "#25101520";
        private string _headerBorderColor = "#20FFFFFF";
        private string _titleColor = "#7DD3FC";
        private double _titleFontSize = 11.0;

        public Guid Id
        {
            get => _id;
            set { _id = value; OnPropertyChanged(); }
        }

        public string PluginId
        {
            get => _pluginId;
            set { _pluginId = value; OnPropertyChanged(); }
        }

        public string GadgetType
        {
            get => _gadgetType;
            set { _gadgetType = value; OnPropertyChanged(); }
        }

        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        public double X
        {
            get => _x;
            set { _x = value; OnPropertyChanged(); }
        }

        public double Y
        {
            get => _y;
            set { _y = value; OnPropertyChanged(); }
        }

        public double Width
        {
            get => _width;
            set { _width = value; OnPropertyChanged(); }
        }

        public double Height
        {
            get => _height;
            set { _height = value; OnPropertyChanged(); }
        }

        public string CustomData
        {
            get => _customData;
            set { _customData = value; OnPropertyChanged(); }
        }

        public bool HideHeader
        {
            get => _hideHeader;
            set { _hideHeader = value; OnPropertyChanged(); }
        }

        public double Opacity
        {
            get => _opacity;
            set { _opacity = value; OnPropertyChanged(); }
        }

        public double MarginLeft
        {
            get => _marginLeft;
            set { _marginLeft = value; OnPropertyChanged(); OnPropertyChanged(nameof(MarginThickness)); }
        }

        public double MarginTop
        {
            get => _marginTop;
            set { _marginTop = value; OnPropertyChanged(); OnPropertyChanged(nameof(MarginThickness)); }
        }

        public double MarginRight
        {
            get => _marginRight;
            set { _marginRight = value; OnPropertyChanged(); OnPropertyChanged(nameof(MarginThickness)); }
        }

        public double MarginBottom
        {
            get => _marginBottom;
            set { _marginBottom = value; OnPropertyChanged(); OnPropertyChanged(nameof(MarginThickness)); }
        }

        public double PaddingLeft
        {
            get => _paddingLeft;
            set { _paddingLeft = value; OnPropertyChanged(); OnPropertyChanged(nameof(PaddingThickness)); }
        }

        public double PaddingTop
        {
            get => _paddingTop;
            set { _paddingTop = value; OnPropertyChanged(); OnPropertyChanged(nameof(PaddingThickness)); }
        }

        public double PaddingRight
        {
            get => _paddingRight;
            set { _paddingRight = value; OnPropertyChanged(); OnPropertyChanged(nameof(PaddingThickness)); }
        }

        public double PaddingBottom
        {
            get => _paddingBottom;
            set { _paddingBottom = value; OnPropertyChanged(); OnPropertyChanged(nameof(PaddingThickness)); }
        }

        public string BgColor
        {
            get => _bgColor;
            set { _bgColor = value; OnPropertyChanged(); }
        }

        public double BgOpacity
        {
            get => _bgOpacity;
            set { _bgOpacity = value; OnPropertyChanged(); }
        }

        public string BorderColor
        {
            get => _borderColor;
            set { _borderColor = value; OnPropertyChanged(); }
        }

        public double BorderThicknessValue
        {
            get => _borderThicknessValue;
            set { _borderThicknessValue = value; OnPropertyChanged(); }
        }

        public double CornerRadiusValue
        {
            get => _cornerRadiusValue;
            set { _cornerRadiusValue = value; OnPropertyChanged(); }
        }

        public string HeaderBgColor
        {
            get => _headerBgColor;
            set { _headerBgColor = value; OnPropertyChanged(); }
        }

        public string HeaderBorderColor
        {
            get => _headerBorderColor;
            set { _headerBorderColor = value; OnPropertyChanged(); }
        }

        public string TitleColor
        {
            get => _titleColor;
            set { _titleColor = value; OnPropertyChanged(); }
        }

        public double TitleFontSize
        {
            get => _titleFontSize;
            set { _titleFontSize = value; OnPropertyChanged(); }
        }

        [Newtonsoft.Json.JsonIgnore]
        public System.Windows.Thickness MarginThickness => new System.Windows.Thickness(MarginLeft, MarginTop, MarginRight, MarginBottom);

        [Newtonsoft.Json.JsonIgnore]
        public System.Windows.Thickness PaddingThickness => new System.Windows.Thickness(PaddingLeft, PaddingTop, PaddingRight, PaddingBottom);

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

