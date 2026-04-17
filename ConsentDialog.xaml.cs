using System.Windows;

namespace Revenant_Theme_Studio
{
    public partial class ConsentDialog : Window
    {
        public ConsentDialog()
        {
            InitializeComponent();
            ConfirmCheck.Checked   += (_, _) => AcceptButton.IsEnabled = true;
            ConfirmCheck.Unchecked += (_, _) => AcceptButton.IsEnabled = false;
        }

        private void Accept_Click(object sender, RoutedEventArgs e)
        {
            Services.ConsentService.Instance.RecordConsent();
            DialogResult = true;
        }

        private void Decline_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
