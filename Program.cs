using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

var builder = WebApplication.CreateBuilder(args);

// Configure HttpClient to ignore SSL certificate errors
builder.Services.AddHttpClient("insecure").ConfigurePrimaryHttpMessageHandler(() =>
{
    return new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
    };
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseRouting();

app.Use(async (context, next) =>
{
    var originalBodyStream = context.Response.Body;
    using (var responseBody = new MemoryStream())
    {
        context.Response.Body = responseBody;
        await next();

        if (context.Response.ContentType != null && context.Response.ContentType.Contains("text/html"))
        {
            context.Response.Body.Seek(0, SeekOrigin.Begin);
            var originalHtml = await new StreamReader(context.Response.Body).ReadToEndAsync();
            context.Response.Body.Seek(0, SeekOrigin.Begin);

            var script = @"
            <script>
                // Override XMLHttpRequest, Fetch, and WebSocket methods
                function overrideRequests(dotnetHost, originalScheme, originalDomainPort) {
                    const originalXHROpen = XMLHttpRequest.prototype.open;
                    const originalFetch = window.fetch;
                    const originalWebSocket = window.WebSocket;

                    XMLHttpRequest.prototype.open = function(method, url, ...rest) {
                        url = transformURL(url, dotnetHost, originalScheme, originalDomainPort);
                        return originalXHROpen.apply(this, [method, url, ...rest]);
                    };

                    window.fetch = function(input, init) {
                        if (typeof input === 'string') {
                            input = transformURL(input, dotnetHost, originalScheme, originalDomainPort);
                        } else if (input instanceof Request) {
                            input = new Request(transformURL(input.url, dotnetHost, originalScheme, originalDomainPort), init || input);
                        }
                        return originalFetch(input, init);
                    };

                    window.WebSocket = function(url, protocols) {
                        url = transformURL(url, dotnetHost.replace(/^http/, 'ws'), originalScheme, originalDomainPort);
                        return new originalWebSocket(url, protocols);
                    };
                }

                // Transform the URL according to the base URL logic
                function transformURL(url, dotnetHost, originalScheme, originalDomainPort) {
                    if (url.startsWith('http') || url.startsWith('https') || url.startsWith('ws') || url.startsWith('wss')) {
                        // Absolute URL with scheme, make it proxyable
                        const urlObj = new URL(url);
                        return `${dotnetHost}/${urlObj.protocol.replace(':', '')}/${urlObj.host}${urlObj.pathname}${urlObj.search}`;
                    } else if (url.startsWith('/')) {
                        // Root-relative URL, modify to match the proxied base URL root
                        return `${dotnetHost}/${originalScheme}/${originalDomainPort}${url}`;
                    }
                    // Relative URL, leave as is
                    return url;
                }

                // Set the base URL and override request methods
                const dotnetHost = window.location.origin;
                const originalURL = new URL(window.location.href);
                const originalScheme = originalURL.protocol.replace(':', '');
                const originalDomainPort = originalURL.host;
                overrideRequests(dotnetHost, originalScheme, originalDomainPort);
            </script>";

            string modifiedHtml;
            if (originalHtml.Contains("<head>"))
            {
                modifiedHtml = originalHtml.Replace("<head>", "<head>" + script);
            }
            else
            {
                modifiedHtml = originalHtml.Replace("<html>", "<html><head>" + script + "</head>");
            }

            var responseBytes = Encoding.UTF8.GetBytes(modifiedHtml);
            context.Response.ContentLength = responseBytes.Length;
            context.Response.Body.SetLength(0);
            await context.Response.Body.WriteAsync(responseBytes, 0, responseBytes.Length);
        }

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        await context.Response.Body.CopyToAsync(originalBodyStream);
    }
});

app.UseEndpoints(endpoints =>
{
    _ = endpoints.Map("{**path}", async context =>
    {
        var clientFactory = context.RequestServices.GetRequiredService<IHttpClientFactory>();
        var client = clientFactory.CreateClient("insecure");

        var originalPath = context.Request.Path.ToString().TrimStart('/');
        var pathSegments = originalPath.Split('/');

        if (pathSegments.Length < 2)
        {
            context.Response.StatusCode = 400; // Bad Request
            await context.Response.WriteAsync("Invalid path format. Expected format: /scheme/domain:port/url_fragments or /domain:port/url_fragments");
            return;
        }

        string scheme = "";
        string domainWithPort = "";
        string urlFragments = "";

        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();

        try
        {
            if (pathSegments[0] == "http" || pathSegments[0] == "https")
            {
                // Full URL is provided in the path
                scheme = pathSegments[0];
                domainWithPort = pathSegments[1];
                urlFragments = string.Join('/', pathSegments.Skip(2));
            }
            else
            {
                // Scheme is not provided, use Referer header to determine the original URL
                if (!context.Request.Headers.TryGetValue("Referer", out var refererValues))
                {
                    context.Response.StatusCode = 400; // Bad Request
                    await context.Response.WriteAsync("Referer header is required for in-page assets without scheme.");
                    return;
                }

                var referer = refererValues.First();
                var refererUri = new Uri(referer);

                var refererPathSegments = refererUri.PathAndQuery.TrimStart('/').Split('/');

                if (refererPathSegments.Length < 2)
                {
                    context.Response.StatusCode = 400; // Bad Request
                    await context.Response.WriteAsync("Invalid referer URL format.");
                    return;
                }

                scheme = refererUri.Scheme;
                domainWithPort = refererPathSegments[0];
                urlFragments = string.Join('/', pathSegments);
            }

            var targetUri = new Uri($"{scheme}://{domainWithPort}/{urlFragments}");

            var requestMessage = new HttpRequestMessage
            {
                Method = new HttpMethod(context.Request.Method),
                RequestUri = targetUri
            };

            // Copy request headers
            foreach (var header in context.Request.Headers)
            {
                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    requestMessage.Headers.Host = targetUri.Host;
                }
                else if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string>)header.Value))
                {
                    requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string>)header.Value);
                }
            }

            // Modify the Origin header if it exists
            if (context.Request.Headers.TryGetValue("Origin", out var originValues))
            {
                var origin = originValues.First();
                var uri = new Uri(origin);
                var manipulatedOrigin = $"{uri.Scheme}://{domainWithPort}";
                requestMessage.Headers.Remove("Origin");
                requestMessage.Headers.Add("Origin", manipulatedOrigin);
            }

            // Logging request details
            logger.LogInformation($"Forwarding request to {targetUri}");

            HttpResponseMessage responseMessage;

            try
            {
                responseMessage = await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error forwarding request");
                context.Response.StatusCode = 502; // Bad Gateway
                await context.Response.WriteAsync("Error forwarding request");
                return;
            }

            context.Response.StatusCode = (int)responseMessage.StatusCode;

            foreach (var header in responseMessage.Headers)
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }

            foreach (var header in responseMessage.Content.Headers)
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }

            // Remove the X-Frame-Options header
            context.Response.Headers.Remove("X-Frame-Options");

            // Set a permissive version of "X-Permitted-Cross-Domain-Policies" header
            context.Response.Headers["X-Permitted-Cross-Domain-Policies"] = "all";

            // Handle cookies
            if (context.Response.Headers.ContainsKey("Set-Cookie"))
            {
                var cookies = context.Response.Headers["Set-Cookie"].ToList();
                context.Response.Headers.Remove("Set-Cookie");
                foreach (var cookie in cookies)
                {
                    var modifiedCookie = cookie;

                    // Set cookie to short-lived
                    if (!modifiedCookie.Contains("Max-Age"))
                    {
                        modifiedCookie += "; Max-Age=5";
                    }

                    // Change domain to dotnet host
                    var domainSegment = modifiedCookie.Split(';').FirstOrDefault(segment => segment.Trim().StartsWith("Domain=", StringComparison.OrdinalIgnoreCase));
                    if (domainSegment != null)
                    {
                        modifiedCookie = modifiedCookie.Replace(domainSegment, $"Domain={context.Request.Host.Host}");
                    }

                    context.Response.Headers.Append("Set-Cookie", modifiedCookie);
                }
            }

            context.Response.Headers.Remove("transfer-encoding");

            await responseMessage.Content.CopyToAsync(context.Response.Body);
        }
        catch (UriFormatException ex)
        {
            logger.LogError(ex, $"Invalid URI format: {scheme}://{domainWithPort}/{urlFragments}");
            context.Response.StatusCode = 400; // Bad Request
            await context.Response.WriteAsync("Invalid URI format.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An unexpected error occurred");
            context.Response.StatusCode = 500; // Internal Server Error
            await context.Response.WriteAsync("An unexpected error occurred.");
        }
    });
});

app.Run();
