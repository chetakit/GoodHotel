
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Models
{
    public class GoodHotelDbContext : DbContext
    {
        public GoodHotelDbContext(DbContextOptions<GoodHotelDbContext> options)
            : base(options)
        {
            // EF ตั้ง savepoint ก่อน/หลังทุก SaveChanges ใน transaction เสีย 2 รอบ (~400ms) ระบบนี้พลาดแล้ว rollback ทั้งก้อน จึงไม่ใช้
            Database.AutoSavepointsEnabled = false;
        }

        // Database Tables
        public DbSet<Booking> Bookings { get; set; }
        public DbSet<BookingStay> BookingStays { get; set; }
        public DbSet<Employee> Employees { get; set; }
        public DbSet<HousekeepingTask> HousekeepingTasks { get; set; }
        public DbSet<HousekeepingTaskEvent> HousekeepingTaskEvents { get; set; }
        public DbSet<MaintenanceReport> MaintenanceReports { get; set; }
        public DbSet<MaintenanceReportEvent> MaintenanceReportEvents { get; set; }
        public DbSet<Payment> Payments { get; set; }
        public DbSet<PaymentEvent> PaymentEvents { get; set; }
        public DbSet<PaymentRefund> PaymentRefunds { get; set; }
        public DbSet<Promotion> Promotions { get; set; }
        public DbSet<Room> Rooms { get; set; }
        public DbSet<User> Users { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.UseCollation("utf8mb4_unicode_ci")
                        .HasCharSet("utf8mb4");

            // Booking
            modelBuilder.Entity<Booking>(entity =>
            {
                entity.HasKey(e => e.BookingId);
                entity.ToTable("bookings");

                entity.HasIndex(e => e.PromotionId);
                entity.HasIndex(e => e.RoomId);
                entity.HasIndex(e => e.UserId);

                entity.Property(e => e.BookingId).ValueGeneratedNever();
                entity.Property(e => e.Amount).HasPrecision(10, 2);
                entity.Property(e => e.Status)
                      .HasMaxLength(20)
                      .IsRequired()
                      .HasDefaultValue(BookingStatuses.Booked);
                entity.Property(e => e.CancelReason).HasMaxLength(20);
                entity.HasIndex(e => new { e.Status, e.CreatedAtUtc });

                entity.HasOne(e => e.Promotion)
                      .WithMany(e => e.Bookings)
                      .HasForeignKey(e => e.PromotionId);

                entity.HasOne(e => e.Room)
                      .WithMany(e => e.Bookings)
                      .HasForeignKey(e => e.RoomId);

                entity.HasOne(e => e.User)
                      .WithMany(e => e.Bookings)
                      .HasForeignKey(e => e.UserId);
            });

            modelBuilder.Entity<BookingStay>(entity =>
            {
                entity.ToTable("booking_stays");
                entity.HasKey(e => e.BookingId);
                entity.Property(e => e.BookingId).ValueGeneratedNever();
                entity.Property(e => e.Status).HasMaxLength(20).IsRequired();
                entity.Property(e => e.CheckInNote).HasMaxLength(2000);
                entity.HasOne(e => e.Booking).WithOne().HasForeignKey<BookingStay>(e => e.BookingId)
                      .OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(e => e.CheckedInByUser).WithMany().HasForeignKey(e => e.CheckedInByUserId)
                      .OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(e => e.CheckedOutByUser).WithMany().HasForeignKey(e => e.CheckedOutByUserId)
                      .OnDelete(DeleteBehavior.Restrict);
            });

            // Employee
            modelBuilder.Entity<Employee>(entity =>
            {
                entity.HasKey(e => e.EmployeeId);
                entity.ToTable("employees");

                entity.Property(e => e.EmployeeId).ValueGeneratedNever();

                entity.Property(e => e.Department)
                      .HasMaxLength(50)
                      .UseCollation("utf8mb3_general_ci")
                      .HasCharSet("utf8mb3");

                entity.Property(e => e.Name)
                      .HasMaxLength(100)
                      .UseCollation("utf8mb3_general_ci")
                      .HasCharSet("utf8mb3");

                entity.Property(e => e.Role)
                      .HasMaxLength(50)
                      .UseCollation("utf8mb3_general_ci")
                      .HasCharSet("utf8mb3");

                entity.Property(e => e.Shift)
                      .HasMaxLength(30)
                      .UseCollation("utf8mb3_general_ci")
                      .HasCharSet("utf8mb3");

                entity.Property(e => e.Status)
                      .HasMaxLength(20)
                      .UseCollation("utf8mb3_general_ci")
                      .HasCharSet("utf8mb3");
            });

            modelBuilder.Entity<HousekeepingTask>(entity =>
            {
                entity.ToTable("housekeeping_tasks");
                entity.HasKey(e => e.TaskId);
                entity.Property(e => e.TaskId).ValueGeneratedOnAdd();
                entity.Property(e => e.TaskType).HasMaxLength(50).IsRequired();
                entity.Property(e => e.Status).HasMaxLength(20).IsRequired();
                entity.HasIndex(e => new { e.StayBookingId, e.TaskType }).IsUnique();
                entity.HasIndex(e => new { e.Status, e.AssignedToUserId });
                entity.HasOne(e => e.Room).WithMany().HasForeignKey(e => e.RoomId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(e => e.AssignedToUser).WithMany().HasForeignKey(e => e.AssignedToUserId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(e => e.CreatedByUser).WithMany().HasForeignKey(e => e.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(e => e.CompletedByUser).WithMany().HasForeignKey(e => e.CompletedByUserId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(e => e.VerifiedByUser).WithMany().HasForeignKey(e => e.VerifiedByUserId).OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<HousekeepingTaskEvent>(entity =>
            {
                entity.ToTable("housekeeping_task_events");
                entity.HasKey(e => e.EventId);
                entity.Property(e => e.EventId).ValueGeneratedOnAdd();
                entity.Property(e => e.EventType).HasMaxLength(50).IsRequired();
                entity.Property(e => e.PreviousStatus).HasMaxLength(20);
                entity.Property(e => e.NewStatus).HasMaxLength(20);
                entity.Property(e => e.Reason).HasMaxLength(2000);
                entity.HasIndex(e => new { e.ActorUserId, e.OccurredAtUtc });
                entity.HasOne(e => e.Task).WithMany(e => e.Events).HasForeignKey(e => e.TaskId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(e => e.ActorUser).WithMany().HasForeignKey(e => e.ActorUserId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(e => e.PreviousAssigneeUser).WithMany().HasForeignKey(e => e.PreviousAssignee).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(e => e.NewAssigneeUser).WithMany().HasForeignKey(e => e.NewAssignee).OnDelete(DeleteBehavior.Restrict);
            });

            // Maintenance report
            modelBuilder.Entity<MaintenanceReport>(entity =>
            {
                entity.ToTable("maintenance_reports");
                entity.HasKey(e => e.ReportId);
                entity.Property(e => e.ReportId).ValueGeneratedOnAdd();
                entity.Property(e => e.Description).HasMaxLength(2000).IsRequired();
                entity.Property(e => e.Status).HasMaxLength(20).IsRequired().IsConcurrencyToken();
                entity.Property(e => e.BlocksRoom).IsConcurrencyToken();
                entity.Property(e => e.BlockFrom).IsConcurrencyToken();
                entity.Property(e => e.BlockUntil).IsConcurrencyToken();
                entity.Property(e => e.ResolutionNote).HasMaxLength(2000);
                entity.HasIndex(e => new { e.RoomId, e.Status, e.BlocksRoom });
                entity.HasOne(e => e.Room).WithMany().HasForeignKey(e => e.RoomId)
                      .OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(e => e.ReportedByUser).WithMany().HasForeignKey(e => e.ReportedByUserId)
                      .OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(e => e.ResolvedByUser).WithMany().HasForeignKey(e => e.ResolvedByUserId)
                      .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<MaintenanceReportEvent>(entity =>
            {
                entity.ToTable("maintenance_report_events");
                entity.HasKey(e => e.EventId);
                entity.Property(e => e.EventId).ValueGeneratedOnAdd();
                entity.Property(e => e.FromStatus).HasMaxLength(20);
                entity.Property(e => e.ToStatus).HasMaxLength(20).IsRequired();
                entity.Property(e => e.Note).HasMaxLength(2000);
                entity.HasIndex(e => new { e.ReportId, e.EventId });
                entity.HasIndex(e => e.ActorUserId);
                entity.HasOne(e => e.Report).WithMany(e => e.Events).HasForeignKey(e => e.ReportId)
                      .OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(e => e.ActorUser).WithMany().HasForeignKey(e => e.ActorUserId)
                      .OnDelete(DeleteBehavior.Restrict);
            });

            // Payment
            modelBuilder.Entity<Payment>(entity =>
            {
                entity.HasKey(e => e.PaymentId);
                entity.ToTable("payments");

                entity.HasIndex(e => e.BookingId);

                entity.Property(e => e.PaymentId).ValueGeneratedNever();
                entity.Property(e => e.Amount).HasPrecision(10, 2);
                entity.Property(e => e.EvidenceFileName).HasMaxLength(100);
                entity.Property(e => e.EvidenceSha256).HasMaxLength(64).IsUnicode(false);
                entity.Property(e => e.LegacyDisposition).HasMaxLength(20);
                entity.HasIndex(e => e.EvidenceSha256).IsUnique();

                entity.Property(e => e.PaymentMethod)
                      .HasMaxLength(50)
                      .UseCollation("utf8mb3_general_ci")
                      .HasCharSet("utf8mb3");

                entity.Property(e => e.PaymentStatus)
                      .HasMaxLength(20)
                      .UseCollation("utf8mb3_general_ci")
                      .HasCharSet("utf8mb3");

                entity.HasOne(e => e.Booking)
                      .WithMany(e => e.Payments)
                      .HasForeignKey(e => e.BookingId);
                entity.HasOne(e => e.SubmittedByUser).WithMany().HasForeignKey(e => e.SubmittedByUserId)
                      .OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(e => e.VerifiedByUser).WithMany().HasForeignKey(e => e.VerifiedByUserId)
                      .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<PaymentEvent>(entity =>
            {
                entity.ToTable("payment_events");
                entity.HasKey(e => e.EventId);
                entity.Property(e => e.EventId).ValueGeneratedOnAdd();
                entity.Property(e => e.EventType).HasMaxLength(30).IsRequired();
                entity.Property(e => e.PaymentMethod).HasMaxLength(50);
                entity.Property(e => e.Reason).HasMaxLength(2000);
                entity.Property(e => e.Amount).HasPrecision(10, 2);
                entity.Property(e => e.IdempotencyKey).HasMaxLength(32).IsUnicode(false).IsRequired();
                entity.HasIndex(e => e.IdempotencyKey).IsUnique();
                entity.HasIndex(e => new { e.PaymentId, e.EventId });
                entity.HasOne(e => e.Payment).WithMany(e => e.Events).HasForeignKey(e => e.PaymentId)
                      .OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(e => e.ActorUser).WithMany().HasForeignKey(e => e.ActorUserId)
                      .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<PaymentRefund>(entity =>
            {
                entity.ToTable("payment_refunds");
                entity.HasKey(e => e.RefundId);
                entity.Property(e => e.RefundId).ValueGeneratedOnAdd();
                entity.Property(e => e.Amount).HasPrecision(10, 2);
                entity.Property(e => e.Status).HasMaxLength(20).IsRequired();
                entity.Property(e => e.Reason).HasMaxLength(2000).IsRequired();
                entity.HasIndex(e => new { e.PaymentId, e.Status });
                entity.HasOne(e => e.Payment).WithMany(e => e.Refunds).HasForeignKey(e => e.PaymentId)
                      .OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(e => e.RequestedByUser).WithMany().HasForeignKey(e => e.RequestedByUserId)
                      .OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(e => e.ApprovedByUser).WithMany().HasForeignKey(e => e.ApprovedByUserId)
                      .OnDelete(DeleteBehavior.Restrict);
            });

            // Promotion
            modelBuilder.Entity<Promotion>(entity =>
            {
                entity.HasKey(e => e.PromotionId);
                entity.ToTable("promotions");

                entity.Property(e => e.PromotionId).ValueGeneratedNever();

                entity.Property(e => e.Benefit)
                      .HasMaxLength(255)
                      .UseCollation("utf8mb3_general_ci")
                      .HasCharSet("utf8mb3");

                entity.Property(e => e.Condition)
                      .HasMaxLength(255)
                      .UseCollation("utf8mb3_general_ci")
                      .HasCharSet("utf8mb3");

                entity.Property(e => e.Description)
                      .HasMaxLength(255)
                      .UseCollation("utf8mb3_general_ci")
                      .HasCharSet("utf8mb3");

                entity.Property(e => e.PromotionName)
                      .HasMaxLength(100)
                      .UseCollation("utf8mb3_general_ci")
                      .HasCharSet("utf8mb3");

                entity.Property(e => e.RuleType).HasMaxLength(30);
                entity.Property(e => e.DiscountType).HasMaxLength(10);
                entity.Property(e => e.DiscountValue).HasPrecision(10, 2);
                entity.Property(e => e.MaxDiscount).HasPrecision(10, 2);
                entity.Property(e => e.MinSpend).HasPrecision(10, 2);
                entity.Property(e => e.IsActive).HasDefaultValue(true);
            });

            // Room
            modelBuilder.Entity<Room>(entity =>
            {
                entity.HasKey(e => e.RoomId);
                entity.ToTable("rooms");

                entity.Property(e => e.RoomId).ValueGeneratedNever();

                entity.Property(e => e.Description)
                      .HasMaxLength(500)
                      .UseCollation("utf8mb3_general_ci")
                      .HasCharSet("utf8mb3");

                entity.Property(e => e.Rate).HasPrecision(10, 2);

                entity.Property(e => e.ImagePath)
                      .HasMaxLength(255)
                      .UseCollation("utf8mb3_general_ci")
                      .HasCharSet("utf8mb3");

                entity.Property(e => e.Room1)
                      .HasMaxLength(20)
                      .HasColumnName("Room")
                      .UseCollation("utf8mb3_general_ci")
                      .HasCharSet("utf8mb3");

                entity.Property(e => e.Status)
                      .HasMaxLength(20)
                      .UseCollation("utf8mb3_general_ci")
                      .HasCharSet("utf8mb3");

                entity.Property(e => e.PreMaintenanceStatus)
                      .HasMaxLength(20)
                      .UseCollation("utf8mb3_general_ci")
                      .HasCharSet("utf8mb3");

                entity.Property(e => e.Type)
                      .HasMaxLength(50)
                      .UseCollation("utf8mb3_general_ci")
                      .HasCharSet("utf8mb3");
            });

            // User
            modelBuilder.Entity<User>(entity =>
            {
                entity.HasKey(e => e.UserId);
                entity.ToTable("users");

                entity.Property(e => e.UserId).ValueGeneratedNever();

                entity.Property(e => e.IsActive).HasDefaultValue(true);

                entity.Property(e => e.Email)
                      .HasMaxLength(100)
                      .UseCollation("utf8mb3_general_ci")
                      .HasCharSet("utf8mb3");

                entity.Property(e => e.FirstName)
                      .HasMaxLength(50)
                      .UseCollation("utf8mb3_general_ci")
                      .HasCharSet("utf8mb3");

                entity.Property(e => e.LastName)
                      .HasMaxLength(50)
                      .UseCollation("utf8mb3_general_ci")
                      .HasCharSet("utf8mb3");

                entity.Property(e => e.Password)
                      .HasMaxLength(255)
                      .UseCollation("utf8mb3_general_ci")
                      .HasCharSet("utf8mb3");

                entity.Property(e => e.Phone)
                      .HasMaxLength(20)
                      .UseCollation("utf8mb3_general_ci")
                      .HasCharSet("utf8mb3");

                entity.Property(e => e.Role)
                      .HasMaxLength(20)
                      .UseCollation("utf8mb3_general_ci")
                      .HasCharSet("utf8mb3");
            });
        }
    }
}
