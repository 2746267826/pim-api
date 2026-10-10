namespace Pim.Api.Services;

/// <summary>
/// 客户端更新检查的数据来源。
///
/// 拆仓后不再读 GitHub release API，而是读各客户端仓在发版时随包上传的
/// <c>version.json</c>，通过 release 的稳定直链获取：
/// https://github.com/{owner}/{repo}/releases/latest/download/version.json
///
/// 好处：不需要 token、不受 API 限流、也不会因为 release 资产命名变化而
/// 解析失败（版本号与下载地址都由发版方显式写入文件）。
/// </summary>
public class GitHubReleaseOptions
{
    /// <summary>Windows 客户端仓（提供 windows / shellWindows 两个组件的版本与下载地址）。</summary>
    public string WindowsRepo { get; set; } = "2746267826/pim-windows";

    /// <summary>Android 客户端仓（提供 android / shellAndroid 两个组件）。</summary>
    public string AndroidRepo { get; set; } = "2746267826/pim-android";

    /// <summary>
    /// 版本文件在 release 里的资产名。
    /// 直链形如 https://github.com/{repo}/releases/latest/download/{VersionFileName}
    /// </summary>
    public string VersionFileName { get; set; } = "version.json";

    /// <summary>
    /// 可选。走 releases/latest/download 直链不需要鉴权；仅当客户端仓为私有仓
    /// 或需要绕过匿名限流时配置（也会作为 Authorization 头发送）。
    /// </summary>
    public string? Token { get; set; }

    /// <summary>轮询间隔。</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromHours(6);
}
