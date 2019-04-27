﻿using FakeServer.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FakeServer.CustomResponse
{
    // TODO: How to name these?
    public class Globals
    {
        public HttpContext _Context;
        public string _CollectionId;
        public string _Body;
    }

    public class CustomResponseMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly CustomResponseSettings _settings;
        private readonly List<Script<object>> _scripts;

        public CustomResponseMiddleware(RequestDelegate next, IOptions<CustomResponseSettings> settings)
        {
            _next = next;
            _settings = settings.Value;
            _scripts = _settings.Scripts.Select(s =>
            {
                var script = CSharpScript.Create<object>(s.Script,
                                                        ScriptOptions.Default
                                                                        .WithReferences(s.References)
                                                                        .WithImports(s.Usings),
                                                        globalsType: typeof(Globals));

                script.Compile();
                return script;
            }).ToList();
        }

        public async Task Invoke(HttpContext context)
        {
            var scriptSettings = _settings.Scripts.LastOrDefault(s =>
                s.Methods.Contains(context.Request.Method) && s.Paths.Any(context.Request.Path.Value.Contains));

            if (scriptSettings == null)
            {
                await _next(context);
                return;
            }

            var idx = _settings.Scripts.IndexOf(scriptSettings);
            var script = _scripts[idx];

            var originalStream = context.Response.Body;

            using (var ms = new MemoryStream())
            {
                context.Response.Body = ms;

                await _next(context);

                var bodyString = Encoding.UTF8.GetString((context.Response.Body as MemoryStream).ToArray());
                var globalObject = new Globals
                {
                    _Context = context,
                    _CollectionId = GetCollectionFromPath(context.Request.Path.Value),
                    _Body = RemoveLiterals(bodyString)
                };

                var scriptResult = await script.RunAsync(globalObject);

                // HACK: Remove string quotemarks from around the _Body
                // Original body is e.g. in an array: [{\"id\":1,\"name\":\"Jame\s\"}]
                // Script will return new { Data = _Body }
                // Script will set Data as a string { Data = "[{\"id\":1,\"name\":\"Jame\s\"}]" }

                var jsonCleared = RemoveLiterals(JsonConvert.SerializeObject(scriptResult.ReturnValue));

                var bodyCleanStart = jsonCleared.IndexOf(globalObject._Body);

                if (bodyCleanStart != -1)
                {
                    var bodyCleanEnd = bodyCleanStart + globalObject._Body.Length;

                    jsonCleared = jsonCleared
                              .Remove(bodyCleanEnd, 1)
                              .Remove(bodyCleanStart - 1, 1);
                }

                var byteArray = Encoding.ASCII.GetBytes(jsonCleared);
                var stream = new MemoryStream(byteArray);
                await stream.CopyToAsync(originalStream);
            }
        }

        // TODO: Move to helper classes and add tests

        private string RemoveLiterals(string input) => Regex.Replace(input, "[\\\\](?=(\"))", "");

        private string GetCollectionFromPath(string path)
        {
            try
            {
                var collection = path.Remove(0, Config.ApiRoute.Length + 2);
                collection = collection.IndexOf("/") != -1 ? collection.Remove(collection.IndexOf("/")) : collection;
                collection = collection.IndexOf("?") != -1 ? collection.Remove(collection.IndexOf("?")) : collection;
                return collection;
            }
            catch
            {
                return string.Empty;
            }
        }

        private class SS : CustomResponseSettings
        {
        }
    }
}