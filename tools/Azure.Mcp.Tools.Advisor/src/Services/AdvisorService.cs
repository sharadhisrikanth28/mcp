// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Azure.Mcp.Core.Services.Azure;
using Azure.Mcp.Core.Services.Azure.Subscription;
using Azure.Mcp.Core.Services.Azure.Tenant;
using Azure.Mcp.Tools.Advisor.Models;
using Azure.ResourceManager.ResourceGraph;
using Azure.ResourceManager.ResourceGraph.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Mcp.Core.Options;

namespace Azure.Mcp.Tools.Advisor.Services;

public class AdvisorService(ISubscriptionService subscriptionService, ITenantService tenantService, ILogger<AdvisorService> logger)
    : BaseAzureResourceService(subscriptionService, tenantService), IAdvisorService
{
    internal const string GroupByRecommendation = "recommendation";
    internal const string GroupByCategory = "category";
    internal const string GroupByImpact = "impact";
    internal const string GroupByResourceType = "resource-type";
    internal const string GroupByResource = "resource";

    internal static readonly IReadOnlyList<string> AllowedGroupBy =
    [
        GroupByRecommendation,
        GroupByCategory,
        GroupByImpact,
        GroupByResourceType,
        GroupByResource,
    ];

    private readonly ISubscriptionService _advisorSubscriptionService = subscriptionService;
    private readonly ILogger<AdvisorService> _logger = logger;

    public async Task<ResourceQueryResults<Recommendation>> ListRecommendationsAsync(
        string subscription,
        string? resourceGroup,
        RetryPolicyOptions? retryPolicy,
        RecommendationFilters? filters = null,
        CancellationToken cancellationToken = default)
    {
        var additionalFilter = BuildAdditionalFilter(filters);

        return await ExecuteResourceQueryAsync(
            "Microsoft.Advisor/recommendations",
            resourceGroup,
            subscription,
            retryPolicy,
            ConvertToAdvisorRecommendationModel,
            tableName: "advisorresources",
            additionalFilter: additionalFilter,
            cancellationToken: cancellationToken);
    }

    public async Task<RecommendationSummary> SummarizeRecommendationsAsync(
        string subscription,
        string? resourceGroup,
        RetryPolicyOptions? retryPolicy,
        string groupBy,
        int top,
        RecommendationFilters? filters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscription);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupBy);

        var subscriptionResource = await _advisorSubscriptionService.GetSubscription(subscription, null, retryPolicy, cancellationToken);
        var allTenants = await TenantService.GetTenants(cancellationToken);
        var tenantResource = allTenants.FirstOrDefault(t => t.Data.TenantId == subscriptionResource!.Data.TenantId)
            ?? throw new InvalidOperationException($"No accessible tenant found for subscription '{subscription}'");

        if (!string.IsNullOrEmpty(resourceGroup))
        {
            var rgExists = await subscriptionResource!.GetResourceGroups().ExistsAsync(resourceGroup, cancellationToken);
            if (!rgExists.Value)
            {
                throw new KeyNotFoundException(
                    $"Resource group '{resourceGroup}' does not exist in subscription '{subscriptionResource.Data.SubscriptionId}'");
            }
        }

        var query = BuildSummarizeQuery(groupBy, resourceGroup, filters);

        var queryContent = new ResourceQueryContent(query)
        {
            Subscriptions = { subscriptionResource!.Data.SubscriptionId }
        };

        ResourceQueryResult result = await tenantResource.GetResourcesAsync(queryContent, cancellationToken);

        var allGroups = new List<RecommendationGroup>();

        if (result?.Count > 0)
        {
            using var jsonDocument = JsonDocument.Parse(result.Data);
            var dataArray = jsonDocument.RootElement;
            if (dataArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dataArray.EnumerateArray())
                {
                    var key = item.TryGetProperty("key", out var keyProp) && keyProp.ValueKind == JsonValueKind.String
                        ? keyProp.GetString() ?? "Unknown"
                        : "Unknown";
                    var count = item.TryGetProperty("count_", out var countProp) ? countProp.GetInt64() : 0;
                    allGroups.Add(new RecommendationGroup(key, (int)count));
                }
            }
        }

        var totalRecommendations = allGroups.Sum(g => g.Count);
        var topGroups = allGroups.Take(top).ToList();

        return new RecommendationSummary(
            GroupBy: groupBy,
            Top: top,
            TotalRecommendations: totalRecommendations,
            AreResultsTruncated: result?.ResultTruncated == ResultTruncated.True,
            Groups: topGroups);
    }

    internal static string BuildSummarizeQuery(string groupBy, string? resourceGroup, RecommendationFilters? filters)
    {
        var query = "advisorresources | where type =~ 'Microsoft.Advisor/recommendations'";

        if (!string.IsNullOrEmpty(resourceGroup))
        {
            query += $" and resourceGroup =~ '{EscapeKqlString(resourceGroup)}'";
        }

        var additionalFilter = BuildAdditionalFilter(filters);
        if (!string.IsNullOrEmpty(additionalFilter))
        {
            query += $" and {additionalFilter}";
        }

        var summarizeField = MapGroupByToKqlField(groupBy);

        query += $" | summarize count() by key={summarizeField}";
        query += " | order by count_ desc, key asc";

        return query;
    }

    internal static string MapGroupByToKqlField(string groupBy) => groupBy.ToLowerInvariant() switch
    {
        GroupByCategory =>
            "iff(isempty(tostring(properties.category)), 'Unknown', tostring(properties.category))",
        GroupByImpact =>
            "iff(isempty(tostring(properties.impact)), 'Unknown', tostring(properties.impact))",
        GroupByRecommendation =>
            "iff(isempty(tostring(properties.shortDescription.problem)), 'Unknown', tostring(properties.shortDescription.problem))",
        GroupByResource =>
            "iff(isempty(tostring(properties.resourceMetadata.resourceId)), 'Unknown', tostring(properties.resourceMetadata.resourceId))",
        GroupByResourceType =>
            "iff(isempty(extract(@'/providers/([^/]+/[^/]+)', 1, tostring(properties.resourceMetadata.resourceId))), 'Unknown', " +
            "extract(@'/providers/([^/]+/[^/]+)', 1, tostring(properties.resourceMetadata.resourceId)))",
        _ => throw new ArgumentException(
            $"Unsupported group-by value '{groupBy}'. Allowed values: {string.Join(", ", AllowedGroupBy)}.",
            nameof(groupBy)),
    };

    internal static string? BuildAdditionalFilter(RecommendationFilters? filters)
    {
        if (filters is null)
        {
            return null;
        }

        var clauses = new List<string>();

        if (!string.IsNullOrWhiteSpace(filters.Category))
        {
            clauses.Add($"tostring(properties.category) =~ '{SanitizeForKql(filters.Category)}'");
        }

        if (!string.IsNullOrWhiteSpace(filters.Impact))
        {
            clauses.Add($"tostring(properties.impact) =~ '{SanitizeForKql(filters.Impact)}'");
        }

        if (!string.IsNullOrWhiteSpace(filters.ResourceType))
        {
            // Match against the ARM id, which contains '/providers/{namespace}/{type}/...'.
            // Case-insensitive contains lets callers pass either the full type
            // ('Microsoft.Storage/storageAccounts') or a fragment ('storageAccounts', 'Storage').
            clauses.Add($"tolower(tostring(properties.resourceMetadata.resourceId)) contains tolower('{SanitizeForKql(filters.ResourceType)}')");
        }

        if (!string.IsNullOrWhiteSpace(filters.Resource))
        {
            clauses.Add($"tolower(tostring(properties.resourceMetadata.resourceId)) contains tolower('{SanitizeForKql(filters.Resource)}')");
        }

        if (!string.IsNullOrWhiteSpace(filters.Search))
        {
            clauses.Add($"tolower(tostring(properties.shortDescription.problem)) contains tolower('{SanitizeForKql(filters.Search)}')");
        }

        return clauses.Count == 0 ? null : string.Join(" and ", clauses);
    }

    // Strip the KQL pipe operator before escaping. BaseAzureResourceService rejects
    // any additionalFilter containing '|' as an injection guard, so allowing it
    // through would surface as a confusing ArgumentException to the caller. Real
    // Advisor categories/impacts never contain '|', and dropping it from free-text
    // search input is a safer default than failing the whole query.
    private static string SanitizeForKql(string value) => EscapeKqlString(value.Replace("|", string.Empty));

    internal static Recommendation ConvertToAdvisorRecommendationModel(JsonElement item)
    {
        Models.RecommendationData? advisorRecommendation = Models.RecommendationData.FromJson(item)
            ?? throw new InvalidOperationException("Failed to parse Advisor recommendation data");

        var resourceId = advisorRecommendation.Properties?.ResourceMetadata?.ResourceId ?? "Unknown";

        return new(
            ResourceId: resourceId,
            RecommendationText: advisorRecommendation.Properties?.ShortDescription?.Problem ?? "Unknown",
            Category: advisorRecommendation.Properties?.Category ?? "Unknown",
            Impact: advisorRecommendation.Properties?.Impact,
            ImpactedResourceType: ParseImpactedResourceType(resourceId));
    }

    internal static string? ParseImpactedResourceType(string? resourceId)
    {
        if (string.IsNullOrEmpty(resourceId))
        {
            return null;
        }

        var segments = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? ns = null;
        var typeParts = new List<string>();

        for (var i = 0; i < segments.Length; i++)
        {
            if (!string.Equals(segments[i], "providers", StringComparison.OrdinalIgnoreCase) || i + 2 >= segments.Length)
            {
                continue;
            }

            ns = segments[i + 1];
            typeParts.Clear();
            typeParts.Add(segments[i + 2]);

            for (var j = i + 4; j < segments.Length; j += 2)
            {
                typeParts.Add(segments[j]);
            }

            break;
        }

        return ns is null ? null : $"{ns}/{string.Join('/', typeParts)}";
    }
}
