using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;
using SenX.Monitor.Contracts;

namespace SenX.Monitor.Magnetar.Reporting;

/// <summary>
/// Turns ids into names for Quasar. History holds numbers only, so Quasar asks for the ids it hasn't
/// seen and keeps the answers. Names live in the game, so this hops onto the game thread, and a
/// batch is capped so a long list can't cost one frame much.
/// </summary>
internal static class IdentityService
{
    public static Task<IdentityResponse> LookupAsync(IdentityRequest request)
    {
        List<IdentityQuery> queries = request.Queries.Take(MonitorProtocol.MaxIdentityQueries).ToList();

        return ServerApi.OnGameThreadAsync(() => Answer(queries), CancellationToken.None);
    }

    private static IdentityResponse Answer(List<IdentityQuery> queries)
    {
        var response = new IdentityResponse();

        foreach (IdentityQuery query in queries)
        {
            response.Answers.Add(new IdentityAnswer
            {
                Kind = query.Kind,
                Key = query.Key,
                Name = LagReport.NameFor(query.Kind, query.Key, query.Representative),
            });
        }

        return response;
    }
}
