using Com.Iotzio.Api;

namespace MauiAppIotzio
{
    public partial class MainPage : ContentPage
    {

        public MainPage()
        {
            InitializeComponent();
        }

        private void OnConnect(object sender, EventArgs e)
        {
            try
            {
                var iotzioManager = new IotzioManager();
                var iotzioInfos = iotzioManager.ListConnectedBoards();

                if (!iotzioInfos.Any()) {
                    LoggingText.Text += "No Iotzio connected!\n";
                }

                foreach (var iotzioInfo in iotzioInfos)
                {
                    using var iotzio = iotzioInfo.Open();

                    LoggingText.Text += $"Found Iotzio {iotzio.Version()} with serial number {iotzio.SerialNumber()}!\n";
                }
            }
            catch (Exception ex)
            {
                LoggingText.Text += $"Exception: {ex}\n";
            }
        }
    }
}
