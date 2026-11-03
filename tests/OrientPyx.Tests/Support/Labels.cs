using System.Linq.Expressions;
using System.Reflection;

namespace OrientPyx.Tests.Support;

/// <summary>
/// Builds any «labels» / «header» record the document builders take, reflectively: a string parameter gets its
/// own name, an enum→string dictionary gets every enum member, a string-returning delegate returns its
/// parameter name, a list is empty. The tests check that the builders run and write valid files, not the
/// wording, which the view models supply from the localization service.
/// </summary>
public static class Labels
{
    public static T Make<T>() => (T)Make(typeof(T));

    private static object Make(Type t)
    {
        var ctor = t.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        return ctor.Invoke(ctor.GetParameters().Select(Value).ToArray());
    }

    private static object? Value(ParameterInfo p)
    {
        var t = p.ParameterType;
        if (t == typeof(string))
            return p.Name;

        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)
            && t.GetGenericArguments()[0].IsEnum && t.GetGenericArguments()[1] == typeof(string))
        {
            var dict = (System.Collections.IDictionary)Activator.CreateInstance(
                typeof(Dictionary<,>).MakeGenericType(t.GetGenericArguments()))!;
            foreach (var v in Enum.GetValues(t.GetGenericArguments()[0]))
                dict[v] = v.ToString()!;
            return dict;
        }

        if (p.HasDefaultValue)
            return p.DefaultValue;
        if (t.IsValueType)
            return Activator.CreateInstance(t);

        if (typeof(Delegate).IsAssignableFrom(t))
        {
            var invoke = t.GetMethod("Invoke")!;
            var parameters = invoke.GetParameters().Select(x => Expression.Parameter(x.ParameterType, x.Name)).ToArray();
            Expression body = invoke.ReturnType == typeof(string)
                ? Expression.Constant(p.Name)
                : Expression.Default(invoke.ReturnType);
            return Expression.Lambda(t, body, parameters).Compile();
        }

        if (t.IsGenericType && t.IsInterface && t.GetGenericArguments().Length == 1)
            return Array.CreateInstance(t.GetGenericArguments()[0], 0);
        if (t.IsInterface)
            return null;
        return Make(t);
    }
}
