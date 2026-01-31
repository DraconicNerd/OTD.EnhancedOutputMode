using System;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.DependencyInjection;
using OpenTabletDriver.Plugin.Platform.Display;
using OpenTabletDriver.Plugin.Platform.Pointer;
using OpenTabletDriver.Plugin.Tablet;
using OTD.EnhancedOutputMode.Output;
using OTD.EnhancedOutputMode.Pointers.VMulti;
using OTD.EnhancedOutputMode.Touch;

namespace VoiDPlugins.OutputMode
{
    [PluginName("Enhanced VMulti Absolute Mode"), SupportedPlatform(PluginPlatform.Windows)]
    public class VMultiAbsoluteMode : EnhancedAbsoluteOutputMode
    {
        private VMultiAbsolutePointer? _pointer;
        private IVirtualScreen? _virtualScreen;
        private MultiTouchHandler? _multiTouchHandler;

        [Resolved]
        public IServiceProvider ServiceProvider
        {
            set => _virtualScreen = (IVirtualScreen)value.GetService(typeof(IVirtualScreen))!;
        }

        public override TabletReference Tablet
        {
            get => base.Tablet;
            set
            {
                base.Tablet = value;
                _pointer = new VMultiAbsolutePointer(value, _virtualScreen!);
            }
        }

        public override IAbsolutePointer Pointer
        {
            get => _pointer!;
            set { }
        }

        protected override MultiTouchHandler? MultiTouchHandler
        {
            get
            {
                if (_multiTouchHandler == null && _virtualScreen != null)
                {
                    _multiTouchHandler = new MultiTouchHandler(_virtualScreen);
                }
                return _multiTouchHandler;
            }
        }
    }
}
