using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Granary.Core.Trailers;

/// <summary>
/// The page a trailer plays in: Granary's own, served by Granary over https from a host of its own, holding YouTube's
/// embedded player in an iframe. The embed needs an origin and a Referer; without them YouTube refuses it with error 153
/// ("video player configuration error"), so the WebView never goes to the embed itself.
/// </summary>
/// <remarks>
/// The page reports what the player says over the IFrame Player API's postMessage channel, which it speaks itself: YouTube's
/// API script (<c>iframe_api</c>) would run in the one document that can message Granary. It carries nothing of the server,
/// the profile or the title, only the validated key.
/// </remarks>
public static class TrailerPage
{
    /// <summary>A name under <c>.example</c>, which never resolves: only Granary answers it (TrailerOverlay's WebResourceRequested).</summary>
    public const string Host = "trailer.granary.example";

    /// <summary>What the WebView asks Granary for: everything on <see cref="Host"/>.</summary>
    public const string Filter = "https://" + Host + "/*";

    private const string EmbedOrigin = "https://" + YouTubeVideo.EmbedHost;

    private const string Style = "html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#000}"
        + "iframe{display:block;width:100%;height:100%;border:0}";

    // Knocks ("listening") until the player answers, then subscribes to its state and errors, as YouTube's widget API does;
    // reports only "ready", "playing" once each, and the player's error codes (TrailerPageMessage).
    private const string Script = """
        (() => {
          const embed = "https://www.youtube-nocookie.com";
          const frame = document.getElementById("player");
          const host = window.chrome && window.chrome.webview;
          const report = (message) => { if (host) { host.postMessage(message); } };
          const send = (message) => {
            if (frame.contentWindow) {
              frame.contentWindow.postMessage(JSON.stringify(Object.assign({ id: 1, channel: "widget" }, message)), embed);
            }
          };
          let heard = false;
          let ready = false;
          let playing = false;
          const listen = () => { if (!heard) { send({ event: "listening" }); } };
          const knock = setInterval(listen, 250);
          frame.addEventListener("load", listen);
          const show = (kind) => {
            if (kind === "ready" && !ready) { ready = true; report({ kind: "ready" }); }
            if (kind === "playing" && !playing) { playing = true; report({ kind: "playing" }); }
          };
          window.addEventListener("message", (event) => {
            if (event.origin !== embed || event.source !== frame.contentWindow || typeof event.data !== "string" || event.data.length > 65536) {
              return;
            }
            let data;
            try { data = JSON.parse(event.data); } catch { return; }
            if (!data || typeof data.event !== "string") {
              return;
            }
            if (!heard) {
              heard = true;
              clearInterval(knock);
              send({ event: "command", func: "addEventListener", args: ["onStateChange"] });
              send({ event: "command", func: "addEventListener", args: ["onError"] });
            }
            const info = data.info;
            switch (data.event) {
              case "initialDelivery":
              case "onReady":
                show("ready");
                break;
              case "onStateChange":
                if (info === 1) { show("playing"); }
                break;
              case "infoDelivery":
                if (info && info.playerState === 1) { show("playing"); }
                break;
              case "onError":
                if (Number.isInteger(info)) { report({ kind: "error", code: Math.min(Math.max(info, 0), 9999) }); }
                break;
            }
          });
        })();
        """;

    /// <summary>The page's own origin; the embed answers only it.</summary>
    public static readonly Uri Origin = new("https://" + Host);

    /// <summary>The page's address: the only one the WebView goes to (<see cref="TrailerWebPolicy.AllowsNavigation"/>).</summary>
    public static readonly Uri Url = new("https://" + Host + "/player");

    /// <summary>
    /// The page's response headers. Its policy runs only its own script and style, frames only YouTube's two embed hosts
    /// (TrailerWebPolicy keeps their frames to the embed's path), and loads nothing else; the Referer that goes with the
    /// embed names only the origin.
    /// </summary>
    public static string Headers { get; } = string.Join("\r\n",
        "Content-Type: text/html; charset=utf-8",
        $"Content-Security-Policy: default-src 'none'; script-src 'sha256-{Hash(Script)}'; style-src 'sha256-{Hash(Style)}'; "
            + $"frame-src {EmbedOrigin} https://www.youtube.com; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
        "Referrer-Policy: strict-origin-when-cross-origin",
        "X-Content-Type-Options: nosniff",
        "Cache-Control: no-store");

    /// <summary>The page for the video <paramref name="key"/>; a key that is not one throws.</summary>
    public static string Html(string key)
    {
        string embed = WebUtility.HtmlEncode(YouTubeVideo.EmbedUrl(YouTubeVideo.Checked(key), Origin).AbsoluteUri);
        return $"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="referrer" content="strict-origin-when-cross-origin">
            <title>Trailer</title>
            <style>{Style}</style>
            </head>
            <body>
            <iframe id="player" src="{embed}" title="YouTube video player" referrerpolicy="strict-origin-when-cross-origin" allow="autoplay; encrypted-media; fullscreen; picture-in-picture" allowfullscreen></iframe>
            <script>{Script}</script>
            </body>
            </html>
            """;
    }

    private static string Hash(string content) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
