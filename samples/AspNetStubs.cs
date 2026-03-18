// Minimal stubs for ASP.NET MVC attributes so the sample compiles standalone.
// In a real project these come from Microsoft.AspNetCore.Mvc.

namespace Microsoft.AspNetCore.Mvc
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public class HttpGetAttribute : System.Attribute
    {
        public HttpGetAttribute(string template) { }
    }

    [System.AttributeUsage(System.AttributeTargets.Method)]
    public class HttpPostAttribute : System.Attribute
    {
        public HttpPostAttribute(string template) { }
    }

    [System.AttributeUsage(System.AttributeTargets.Method)]
    public class HttpPutAttribute : System.Attribute
    {
        public HttpPutAttribute(string template) { }
    }

    [System.AttributeUsage(System.AttributeTargets.Method)]
    public class HttpDeleteAttribute : System.Attribute
    {
        public HttpDeleteAttribute(string template) { }
    }

    [System.AttributeUsage(System.AttributeTargets.Parameter)]
    public class FromBodyAttribute : System.Attribute { }

    [System.AttributeUsage(System.AttributeTargets.Parameter)]
    public class FromQueryAttribute : System.Attribute { }

    [System.AttributeUsage(System.AttributeTargets.Parameter)]
    public class FromRouteAttribute : System.Attribute { }
}
