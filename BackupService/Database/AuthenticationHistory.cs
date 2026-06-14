using Microsoft.EntityFrameworkCore;

namespace BackupService.Database
{
    public enum AuthenticationEventType
    {
        LoginSucceeded = 0,
        LoginFailed = 1,
        PasswordChanged = 2,
    }

    /// <summary>
    /// Audit record of an authentication event (login success/failure, password change).
    /// </summary>
    [Index(nameof(TimestampUtc))]
    public class AuthenticationHistory
    {
        public int Id { get; set; }

        public AuthenticationEventType EventType { get; set; }

        public DateTimeOffset TimestampUtc { get; set; }
    }
}
