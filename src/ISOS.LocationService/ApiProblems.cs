using Microsoft.AspNetCore.Mvc;

namespace ISOS.LocationService;

/// <summary>Error responses shared by the controllers.</summary>
public static class ApiProblems
{
    /// <summary>403 with a machine-readable <c>code</c> (LLD 4.1) and, when given, the current policy so the device can stop.</summary>
    public static ObjectResult Forbidden(string code, string detail, object? policy = null)
    {
        var problem = new ProblemDetails { Title = code, Detail = detail, Status = StatusCodes.Status403Forbidden };
        problem.Extensions["code"] = code;
        if (policy is not null)
        {
            problem.Extensions["policy"] = policy;
        }

        return new ObjectResult(problem) { StatusCode = StatusCodes.Status403Forbidden };
    }

    public static BadRequestObjectResult BadRequest(string detail) =>
        new(new ProblemDetails { Title = "Invalid request", Detail = detail, Status = StatusCodes.Status400BadRequest });
}
