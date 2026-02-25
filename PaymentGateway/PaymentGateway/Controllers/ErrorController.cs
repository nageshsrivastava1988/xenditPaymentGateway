using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;

namespace PaymentGateway.Controllers;

[AllowAnonymous]
public class ErrorController : Controller
{
    [Route("error")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Index()
    {
        return View("Error", new ErrorViewModel
        {
            RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
        });
    }
}
