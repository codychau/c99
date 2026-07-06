using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace C99
{
    public partial class App : Application
    {
        private MainWindow? _window;

        public App()
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            var currentInstance = AppInstance.GetCurrent();
            var instances = AppInstance.GetInstances();

            if (instances.Count > 1)
            {
                var otherInstance = instances.First(i => !i.IsCurrent);
                var activatedArgs = currentInstance.GetActivatedEventArgs();
                otherInstance.RedirectActivationToAsync(activatedArgs).AsTask().Wait(3000);
                Environment.Exit(0);
                return;
            }

            currentInstance.Activated += OnActivated;

            _window = new MainWindow();
            _window.Activate();
        }

        private void OnActivated(object? sender, AppActivationArguments e)
        {
            if (_window != null)
                _window.DispatcherQueue.TryEnqueue(() => _window.RestoreFromTray());
        }
    }
}
