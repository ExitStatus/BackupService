using BackupService.Enumerations;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Database
{
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
