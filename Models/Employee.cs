using System;
using System.Collections.Generic;

namespace GoodHotel.Models;

public partial class Employee
{
    public int EmployeeId { get; set; }

    public string? Name { get; set; }

    public string? Department { get; set; }

    public string? Role { get; set; }

    public string? Status { get; set; }

    public string? Shift { get; set; }
}
