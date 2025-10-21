namespace AkariApi.Attributes
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
    public class OptionalTokenRefreshAttribute : Attribute
    {
    }
}