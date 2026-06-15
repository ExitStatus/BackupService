using BackupService.Components.Controls;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Profiles;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Pages.BackupServicePage
{
    public partial class Profiles : ComponentBase
    {
        private const int PageSize = 10;

        [Inject]
        private IProfileService ProfileService { get; set; } = default!;

        private bool _showDialog;
        private int? _editId;
        private Profile? _deleteTarget;
        private Notification _notification = default!;

        private int _page = 1;
        private ProfileSortColumn _sortColumn = ProfileSortColumn.Name;
        private bool _descending;
        private PagedResult<Profile>? _profiles;

        protected override Task OnInitializedAsync() => LoadAsync(_page);

        private async Task LoadAsync(int page)
        {
            _profiles = await ProfileService.GetPageAsync(page, PageSize, _sortColumn, _descending);
            _page = _profiles.PageNumber;
        }

        private async Task SortByAsync(ProfileSortColumn column)
        {
            if (_sortColumn == column)
            {
                _descending = !_descending;
            }
            else
            {
                _sortColumn = column;
                _descending = false;
            }

            await LoadAsync(1);
        }

        private string SortIndicator(ProfileSortColumn column) =>
            _sortColumn != column ? string.Empty : _descending ? " ▼" : " ▲";

        private async Task PreviousPageAsync()
        {
            if (_page > 1)
            {
                await LoadAsync(_page - 1);
            }
        }

        private async Task NextPageAsync()
        {
            if (_profiles is not null && _page < _profiles.TotalPages)
            {
                await LoadAsync(_page + 1);
            }
        }

        private void OpenCreate()
        {
            _editId = null;
            _showDialog = true;
        }

        private void OpenEdit(int id)
        {
            _editId = id;
            _showDialog = true;
        }

        private async Task OnSaved()
        {
            var message = _editId is null ? "Profile created" : "Profile updated";
            _showDialog = false;
            _notification.Show(message, NotificationLevel.Success);
            await LoadAsync(_page);
        }

        private void OpenDelete(Profile profile) => _deleteTarget = profile;

        private async Task ConfirmDeleteAsync()
        {
            if (_deleteTarget is null)
            {
                return;
            }

            await ProfileService.DeleteAsync(_deleteTarget.Id);
            _deleteTarget = null;
            _notification.Show("Profile deleted", NotificationLevel.Success);

            // Step back a page if we just removed the last row on it.
            if (_profiles is not null && _profiles.Items.Count == 1 && _page > 1)
            {
                _page--;
            }

            await LoadAsync(_page);
        }

        private static string DescribeStatus(ProfileStatus status) => status switch
        {
            ProfileStatus.Idle => "Idle",
            ProfileStatus.Running => "Running",
            ProfileStatus.Error => "Error",
            _ => status.ToString(),
        };
    }
}
