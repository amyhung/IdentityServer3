﻿/*
 * Copyright 2014, 2015 Dominick Baier, Brock Allen
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *   http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using FluentAssertions;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Web;
using System;
using System.Text;
using System.Threading.Tasks;
using Thinktecture.IdentityServer.Core.Models;
using Thinktecture.IdentityServer.Core.Extensions;
using Thinktecture.IdentityServer.Core;
using Microsoft.Owin;
using Thinktecture.IdentityServer.Core.ViewModels;
using System.Net.Http.Formatting;
using System.Collections.Specialized;
using System.ComponentModel;
using Thinktecture.IdentityServer.Core.Configuration.Hosting;
using System.Collections.ObjectModel;

namespace Thinktecture.IdentityServer.Tests.Conformance
{
    public static class Extensions
    {
        public static void Login(this IdentityServerHost host, string username = "bob")
        {
            var resp = host.GetLoginPage();
            var model = resp.GetModel<LoginViewModel>();

            var user = host.Users.Single(x=>x.Username == username);
            resp = host.PostForm(model.LoginUrl, new LoginCredentials { Username = user.Username, Password = user.Password }, model.AntiForgery);
            resp.AssertCookie(Constants.PrimaryAuthenticationType);
        }

        public static HttpResponseMessage GetLoginPage(this IdentityServerHost host, SignInMessage msg = null)
        {
            msg = msg ?? new SignInMessage() { ReturnUrl = host.Url.EnsureTrailingSlash() };
            var signInId = host.WriteMessageToCookie(msg);
            return host.Get(host.GetLoginUrl(signInId));
        }

        public static string WriteMessageToCookie<T>(this IdentityServerHost host, T msg)
            where T : Message
        {
            var request_headers = new Dictionary<string, string[]>();
            var response_headers = new Dictionary<string, string[]>();
            var env = new Dictionary<string, object>()
            {
                {"owin.RequestScheme", "https"},
                {"owin.RequestHeaders", request_headers},
                {"owin.ResponseHeaders", response_headers},
                {Constants.OwinEnvironment.IdentityServerBasePath, "/"},
            };

            var ctx = new OwinContext(env);
            var signInCookie = new MessageCookie<T>(ctx, host.Options);
            var id = signInCookie.Write(msg);

            CookieHeaderValue cookie;
            if (!CookieHeaderValue.TryParse(response_headers["Set-Cookie"].First(), out cookie))
            {
                throw new InvalidOperationException("MessageCookie failed to issue cookie");
            }

            host.Client.AddCookies(cookie.Cookies);

            return id;
        }

        public static HttpResponseMessage Get(this IdentityServerHost host, string path)
        {
            if (!path.StartsWith("http")) path = host.Url.EnsureTrailingSlash() + path;
            
            var result = host.Client.GetAsync(path).Result;
            host.ProcessCookies(result);

            return result;
        }

        public static T Get<T>(this IdentityServerHost host, string path)
        {
            var result = host.Get(path);
            result.IsSuccessStatusCode.Should().BeTrue();
            return result.Content.ReadAsAsync<T>().Result;
        }

        static NameValueCollection Map(object values, AntiForgeryTokenViewModel xsrf = null)
        {
            var coll = values as NameValueCollection;
            if (coll != null) return coll;

            coll = new NameValueCollection();
            foreach (PropertyDescriptor descriptor in TypeDescriptor.GetProperties(values))
            {
                var val = descriptor.GetValue(values);
                if (val == null) val = "";
                coll.Add(descriptor.Name, val.ToString());
            }
            
            if (xsrf != null)
            {
                coll.Add(xsrf.Name, xsrf.Value);
            }
            
            return coll;
        }

        static string ToFormBody(NameValueCollection coll)
        {
            var sb = new StringBuilder();
            foreach (var item in coll.AllKeys)
            {
                if (sb.Length > 0)
                {
                    sb.Append("&");
                }
                sb.AppendFormat("{0}={1}", item, coll[item].ToString());
            }
            return sb.ToString();
        }

        public static HttpResponseMessage PostForm(this IdentityServerHost host, string path, object value, AntiForgeryTokenViewModel xsrf = null)
        {
            if (!path.StartsWith("http")) path = host.Url.EnsureTrailingSlash() + path;
            
            var form = Map(value, xsrf);
            var body = ToFormBody(form);
            var content = new StringContent(body, Encoding.UTF8, FormUrlEncodedMediaTypeFormatter.DefaultMediaType.MediaType);
            
            var response = host.Client.PostAsync(path, content).Result;
            host.ProcessCookies(response);

            return response;
        }

        public static HttpResponseMessage PostJson<T>(this IdentityServerHost host, string path, T value)
        {
            return host.Client.PostAsJsonAsync(path, value).Result;
        }

        static void ProcessCookies(this IdentityServerHost host, HttpResponseMessage response)
        {
            var cookies = response.GetCookies();
            foreach(var cookie in cookies)
            {
                if (cookie.Expires != null && cookie.Expires < host.UtcNow)
                {
                    var names = cookie.Cookies.Select(x=>x.Name);
                    host.Client.RemoveCookies(names);
                }
                else
                {
                    host.Client.AddCookies(cookie.Cookies);
                }
            }
        }

        public static string GetLoginUrl(this IdentityServerHost host, string signInId)
        {
            return host.Url.EnsureTrailingSlash() + Constants.RoutePaths.Login + "?signin=" + signInId;
        }
        public static string GetAuthorizeUrl(this IdentityServerHost host)
        {
            return host.Url.EnsureTrailingSlash() + Constants.RoutePaths.Oidc.Authorize;
        }
        public static string GetTokenUrl(this IdentityServerHost host)
        {
            return host.Url.EnsureTrailingSlash() + Constants.RoutePaths.Oidc.Token;
        }
        public static string GetUserInfoUrl(this IdentityServerHost host)
        {
            return host.Url.EnsureTrailingSlash() + Constants.RoutePaths.Oidc.UserInfo;
        }
        public static string GetDiscoveryUrl(this IdentityServerHost host)
        {
            return host.Url.EnsureTrailingSlash() + Constants.RoutePaths.Oidc.DiscoveryConfiguration;
        }

        public static T GetModel<T>(string html)
        {
            var match = "<script id='modelJson' type='application/json'>";
            var start = html.IndexOf(match);
            var end = html.IndexOf("</script>", start);
            var content = html.Substring(start + match.Length, end - start - match.Length);
            var json = HttpUtility.HtmlDecode(content);
            return JsonConvert.DeserializeObject<T>(json);
        }

        public static T GetModel<T>(this HttpResponseMessage resp)
        {
            var html = resp.Content.ReadAsStringAsync().Result;
            return GetModel<T>(html);
        }

        public static void AssertPage(this HttpResponseMessage resp, string name)
        {
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            resp.Content.Headers.ContentType.MediaType.Should().Be("text/html");
            var html = resp.Content.ReadAsStringAsync().Result;

            var match = Regex.Match(html, "<div class='container page-(.*)' ng-cloak>");
            match.Groups[1].Value.Should().Be(name);
        }

        public static void AssertCookie(this HttpResponseMessage resp, string name)
        {
            var cookies = resp.GetCookies();
            var cookie = cookies.SingleOrDefault(x => x.Cookies.Any(y=>y.Name == name));
            cookie.Should().NotBeNull();
        }

        public static void AddCookies(this HttpClient client, IEnumerable<string> cookies)
        {
            foreach (var c in cookies)
            {
                client.DefaultRequestHeaders.Add("Cookie", c);
            }
        }

        public static void AddCookies(this HttpClient client, IEnumerable<CookieState> cookies)
        {
            foreach (var c in cookies)
            {
                client.DefaultRequestHeaders.Add("Cookie", c.ToString());
            }
        }

        public static void RemoveCookies(this HttpClient client, IEnumerable<string> names)
        {
            foreach(var name in names)
            {
                client.RemoveCookie(name);
            }
        }

        public static void RemoveCookie(this HttpClient client, string name)
        {
            var cookies = client.DefaultRequestHeaders.Where(x => x.Key == "Cookie").ToArray();
            client.DefaultRequestHeaders.Remove("Cookie");

            var cookieValues = cookies.SelectMany(x=>x.Value).Where(x=>!x.StartsWith(name+"="));
            client.AddCookies(cookieValues);
        }

        public static IEnumerable<CookieHeaderValue> GetCookies(this HttpResponseMessage resp)
        {
            IEnumerable<string> values;
            if (resp.Headers.TryGetValues("Set-Cookie", out values))
            {
                var cookies = new List<CookieHeaderValue>();
                foreach (var value in values)
                {
                    CookieHeaderValue cookie;
                    if (CookieHeaderValue.TryParse(value, out cookie))
                    {
                        cookies.Add(cookie);
                    }
                }
                return cookies;
            }
            return Enumerable.Empty<CookieHeaderValue>();
        }
    }
}
