using WindowsIsland.Services;

internal static class ToastImageChecks
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WindowsIsland.ToastImages"));
        var photo = Path.Combine(root, "会话头像");
        var xml = $"""
            <toast><visual><binding template="ToastGeneric">
              <text>小林</text><text>你好，稍后见。</text>
              <image placement="appLogoOverride" hint-crop="circle" src="{photo}" />
              <image placement="hero" src="attachment.png" />
            </binding></visual></toast>
            """;
        var image = ToastImageReference.Parse(xml)!;
        check(image.Title == "小林" && image.Body == "你好，稍后见。" && image.LocalPath() == photo,
            "System toast keeps its conversation photo, including extensionless QQ cache files");
        var notification = new IslandNotification(1, DateTimeOffset.UtcNow, "QQ", "小林", "你好，\n稍后见。", AppId: "QQ");
        check(ToastImageReference.Match(notification, [image]) == image,
            "Avatar lookup matches the listener's title and body despite whitespace formatting");
        check(ToastImageReference.Match(notification, [image with { Title = "小红" }]) is null
            && ToastImageReference.Match(notification, [image with { Body = "另一条消息" }]) is null,
            "Photos from another sender or message cannot be attached to a notification");
        check(ToastImageReference.Match(notification, [image, image with { Avatar = "another-photo.png" }]) is null,
            "Identical text with conflicting portraits does not select an arbitrary avatar");
        check(ToastImageReference.Match(notification, [image, image]) == image,
            "Duplicate history entries sharing the same image are safe to resolve");
        var inline = ToastImageReference.Parse("<toast><visual><binding template='ToastGeneric'><text>消息</text><image src='attachment.png'/><image placement='hero' src='hero.png'/></binding></visual></toast>");
        check(inline?.Avatar is null, "Inline attachments and hero pictures are never presented as conversation avatars");
        var legacy = ToastImageReference.Parse("<toast><visual><binding template='ToastImageAndText02'><image src='photo.png'/><text>消息</text></binding></visual></toast>");
        check(legacy?.Avatar == "photo.png", "Legacy toast image templates retain their leading photo");
        check(ToastImageReference.Parse("<toast>") is null
            && ToastImageReference.Parse("<!DOCTYPE toast [<!ENTITY x SYSTEM 'file:///private'>]><toast>&x;</toast>") is null,
            "Malformed payloads and external XML entities are rejected");
        check((image with { Avatar = new Uri(photo).AbsoluteUri }).LocalPath() == photo,
            "File URLs preserve Chinese avatar filenames");
        check((image with { Avatar = "photo.png", BaseUri = new Uri(root + Path.DirectorySeparatorChar).AbsoluteUri }).LocalPath() == Path.Combine(root, "photo.png"),
            "Relative avatar sources resolve against the notification's base URI");
        check((image with { Avatar = "ms-appx:///Assets/photo.png" }).LocalPath(root) == Path.Combine(root, "Assets", "photo.png"),
            "Packaged avatar sources resolve inside the sender's package");
        check((image with { Avatar = "ms-appdata:///local/photo.png" }).LocalPath(applicationDataFolder: root) == Path.Combine(root, "LocalState", "photo.png"),
            "Application data avatar sources resolve to LocalState");
        check((image with { Avatar = "ms-appx:///Assets/%2e%2e%5c%2e%2e%5cprivate.png" }).LocalPath(root) is null,
            "Encoded paths cannot escape a package image folder");
        check((image with { Avatar = "file://server/share/photo.png" }).LocalPath() is null
            && (image with { Avatar = "\\\\server\\share\\photo.png" }).LocalPath() is null
            && (image with { Avatar = "https://example.com/photo.png" }).LocalPath() is null,
            "Image lookup does not open network shares or download unrelated remote images");
    }
}
