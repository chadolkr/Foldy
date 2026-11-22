using Foldy.Core.Localization;
using Foldy.Core.Space;
using Foldy.Presentation.Files;

namespace Foldy.Presentation.Services;

/// <summary>공간이 모자랄 때 쓰는 쉬운 말 (사용자 원칙 2026-11-22: 시작 전에 "얼마 필요, 얼마 남음"을 알린다).</summary>
public static class SpaceText
{
    /// <summary>"D: 드라이브" 또는 "서버 example.com".</summary>
    public static string Place(SpaceCheck c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return c.Server is { } s ? Loc.F("space.server", s) : Loc.F("space.drive", c.DriveName);
    }

    /// <summary>무엇이 모자라고 어떻게 하면 되는지 (상태 줄·안내 창).</summary>
    public static string Explain(SpaceCheck c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return c.Problem switch
        {
            SpaceProblem.Fat32TooBig => Loc.F("space.fat32", FileItem.FormatBytes(c.Need), c.DriveName),
            SpaceProblem.NotEnoughSpace when c.Free < 0 => Loc.F("space.noRoomNoFree", FileItem.FormatBytes(c.Need), Place(c)),
            SpaceProblem.NotEnoughSpace => Loc.F("space.noRoom", FileItem.FormatBytes(c.Need), FileItem.FormatBytes(c.Free), Place(c)),
            _ => "",
        };
    }

    /// <summary>작업 창 한 줄에 들어가는 짧은 말.</summary>
    public static string Brief(SpaceCheck c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return c.Problem == SpaceProblem.Fat32TooBig ? Loc.F("space.shortFat32", FileItem.FormatBytes(c.Need))
            : c.Free < 0 ? Loc.F("space.shortNoFree", FileItem.FormatBytes(c.Need))
            : Loc.F("space.short", FileItem.FormatBytes(c.Need), FileItem.FormatBytes(c.Free));
    }
}
