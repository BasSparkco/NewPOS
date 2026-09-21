using System.ComponentModel.DataAnnotations;

namespace POS.Web.Models;

public sealed class LoginViewModel
{
    /// <summary>Optional — only required once a deployment has more than one tenant to resolve between.</summary>
    [Display(Name = "Business")]
    public string? TenantSlug { get; set; }

    [Required(ErrorMessage = "Username is required.")]
    [Display(Name = "Username")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password is required.")]
    [DataType(DataType.Password)]
    [Display(Name = "Password")]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "Keep me signed in")]
    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; set; }
}