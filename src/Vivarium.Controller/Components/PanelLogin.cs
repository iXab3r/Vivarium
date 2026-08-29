using System.Net;

namespace Vivarium.Controller.Components;

internal static class PanelLogin
{
    public static string Render(bool invalid) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Sign in · Vivarium</title>
            <link rel="stylesheet" href="/app.css">
        </head>
        <body class="login-page">
            <main class="login-shell">
                <div class="login-brand"><span class="brand-mark">V</span><span>Vivarium</span></div>
                <section class="login-copy">
                    <p class="eyebrow">Controller access</p>
                    <h1>Enter the farm.</h1>
                    <p>Sign in with your Vivarium administrator account.</p>
                </section>
                <form class="login-form" method="post" action="/login">
                    <label for="login">Login</label>
                    <input id="login" name="login" autocomplete="username" autofocus required>
                    <label for="password">Password</label>
                    <input id="password" name="password" type="password" autocomplete="current-password" required>
                    {{(invalid ? "<p class=\"login-error\" role=\"alert\">Those credentials were not accepted.</p>" : string.Empty)}}
                    <button class="primary-button" type="submit">Sign in</button>
                </form>
                <details class="login-form">
                    <summary>Legacy administrator token</summary>
                    <form method="post" action="/login">
                        <label for="token">Administrator token</label>
                        <input id="token" name="token" type="password" autocomplete="off" required>
                        <button class="primary-button" type="submit">Use legacy token</button>
                    </form>
                </details>
            </main>
        </body>
        </html>
        """;
}
