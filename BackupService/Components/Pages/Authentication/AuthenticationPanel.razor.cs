using BackupService.Authentication;
using BackupService.Components.Controls;
using BackupService.Database;
using BackupService.Enumerations;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Pages.Authentication
{
    /// <summary>
    /// Settings panel for the single admin login: explains the account, lets the admin
    /// change the password via a modal (confirmed with a toast), and shows a paged
    /// table of authentication history.
    /// </summary>
    public partial class AuthenticationPanel : ComponentBase
    {
        private const int PageSize = 10;

        [Inject]
        private IAuthenticationHistoryService History { get; set; } = default!;

        private bool _showDialog;
        private Notification _notification = default!;
        private int _page = 1;
        private PagedResult<AuthenticationHistory>? _history;

        protected override Task OnInitializedAsync() => LoadAsync(_page);

        private async Task LoadAsync(int page)
        {
            _history = await History.GetPageAsync(page, PageSize);
            _page = _history.PageNumber;
        }

        private async Task PreviousPageAsync()
        {
            if (_page > 1)
            {
                await LoadAsync(_page - 1);
            }
        }

        private async Task NextPageAsync()
        {
            if (_history is not null && _page < _history.TotalPages)
            {
                await LoadAsync(_page + 1);
            }
        }

        private void OpenDialog() => _showDialog = true;

        private void CloseDialog() => _showDialog = false;

        private async Task OnSaved()
        {
            _showDialog = false;
            _notification.Show("Password changed successfully", NotificationLevel.Success);
            await LoadAsync(_page);
        }

        private static string DescribeEvent(AuthenticationEventType type) => type switch
        {
            AuthenticationEventType.LoginSucceeded => "Login succeeded",
            AuthenticationEventType.LoginFailed => "Login failed",
            AuthenticationEventType.PasswordChanged => "Password changed",
            _ => type.ToString(),
        };
    }
}
