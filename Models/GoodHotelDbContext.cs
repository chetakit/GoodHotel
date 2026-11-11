
using Microsoft.EntityFrameworkCore;

namespace GoodHotel.Models
{
    public class GoodHotelDbContext : DbContext
    {
        public GoodHotelDbContext(DbContextOptions<GoodHotelDbContext> options)
            : base(options)
        {
        }

        // Database Tables
        public DbSet<Booking> Bookings { get; set; }
        public DbSet<Employee> Employees { get; set; }
        public DbSet<Payment> Payments { get; set; }
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

            // Payment
            modelBuilder.Entity<Payment>(entity =>
            {
                entity.HasKey(e => e.PaymentId);
                entity.ToTable("payments");

                entity.HasIndex(e => e.BookingId);

                entity.Property(e => e.PaymentId).ValueGeneratedNever();
                entity.Property(e => e.Amount).HasPrecision(10, 2);

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
