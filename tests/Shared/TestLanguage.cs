using System.Reflection;
using System.Runtime.CompilerServices;

namespace Foldy.Tests.Shared;

/// <summary>
/// 시험은 한국어 글을 기준으로 쓴다 (화면 글이 다국어로 옮겨져도 기존 시험이 그대로 맞게).
/// Foldy.Core를 쓰지 않는 시험 프로젝트도 있어 리플렉션으로 부른다. 언어를 바꿔 보는 시험은 [Collection("Loc")]로 혼자 돈다.
/// </summary>
internal static class TestLanguage
{
    [ModuleInitializer]
    internal static void Init()
    {
        try
        {
            var loc = Type.GetType("Foldy.Core.Localization.Loc, Foldy.Core");
            loc?.GetMethod("Use", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, ["ko", null]);
        }
        catch (Exception ex) when (ex is System.IO.FileNotFoundException or TargetInvocationException)
        {
        }
    }
}
