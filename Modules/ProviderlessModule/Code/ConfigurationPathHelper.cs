using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;

namespace ProviderlessModule.Code;

public class ConfigurationPathHelper
{
    public static string GetPath<T, TProperty>(Expression<Func<T, TProperty>> expression)
    {
        if (expression.Body is MemberExpression member)
        {
            // Returns "PortalAccessSettings:TunnelToken"
            return $"{typeof(T).Name}:{member.Member.Name}";
        }

        throw new ArgumentException("Expression must be a property access.");
    }
}
