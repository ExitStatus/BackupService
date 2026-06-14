using BackupService.Components.Controls;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Pages.Authentication
{
    /// <summary>
    /// Settings panel for the single admin login. Explains the account and lets the
    /// admin change the password via a modal, confirming success with a toast.
    /// </summary>
    public partial class AuthenticationPanel : ComponentBase
    {
        private bool _showDialog;
        private Notification _notification = default!;

        private void OpenDialog() => _showDialog = true;

        private void CloseDialog() => _showDialog = false;

        private void OnSaved()
        {
            _showDialog = false;
            _notification.Show("Password changed successfully", NotificationLevel.Success);
        }
    }
}
