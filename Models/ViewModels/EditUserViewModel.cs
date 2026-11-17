using System.ComponentModel.DataAnnotations;

namespace GoodHotel.Models.ViewModels;

public class EditUserViewModel
{
    public int UserId { get; set; }

    [Required, StringLength(50)]
    public string? FirstName { get; set; }

    [Required, StringLength(50)]
    public string? LastName { get; set; }

    [Required, StringLength(100), EmailAddress]
    public string? Email { get; set; }

    [Required, StringLength(20)]
    public string? Phone { get; set; }

    [Required, StringLength(20)]
    public string? Role { get; set; }
}
