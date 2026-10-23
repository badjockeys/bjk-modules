using System.Reflection;
using BJK.Modules.Posts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace BJK.Modules.Posts.Tests.Api;

/// <summary>
/// ASP.NET Core requires every [Authorize] on an action and its controller, so a public read inside a controller that
/// requires sign-in is never public. That made For you refuse logged-out visitors (24 Oct 2026).
/// </summary>
public sealed class PublicReadPolicyTests
{
    public static TheoryData<string> PublicActions() => new(PublicActionNames());

    private static IEnumerable<string> PublicActionNames() =>
        Actions()
            .Where(a => Policies(a.Action).Contains(PostsModule.ReadPolicy))
            .Select(a => $"{a.Controller.Name}.{a.Action.Name}");

    [Theory]
    [MemberData(nameof(PublicActions))]
    public void Public_reads_are_not_inside_a_controller_that_requires_sign_in(string name)
    {
        var (controller, action) = Actions().Single(a => $"{a.Controller.Name}.{a.Action.Name}" == name);

        Assert.All(Policies(controller), policy => Assert.Equal(PostsModule.ReadPolicy, policy));
        Assert.All(Policies(action), policy => Assert.Equal(PostsModule.ReadPolicy, policy));
    }

    [Fact]
    public void For_you_is_public()
    {
        Assert.Contains("TimelinesController.ForYou", PublicActionNames());
    }

    private static IEnumerable<(Type Controller, MethodInfo Action)> Actions() =>
        typeof(PostsModule).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(m => (t, m)));

    private static string?[] Policies(MemberInfo member) =>
        member.GetCustomAttributes<AuthorizeAttribute>(inherit: false).Select(a => a.Policy).ToArray();
}
