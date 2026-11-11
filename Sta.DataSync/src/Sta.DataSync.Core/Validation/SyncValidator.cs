using Sta.DataSync.Planning;

namespace Sta.DataSync.Validation;

/// <summary>Checks scopes and plans against provider and data source capabilities before anything is executed.</summary>
internal static class SyncValidator
{
    /// <summary>Throws when the scope cannot be synchronized safely in its mode.</summary>
    public static void ValidateScope(SyncScopeDefinition scope, SyncMode mode, ConflictResolutionStrategy strategy, bool hasCustomResolver)
    {
        SyncProviderCapabilities capabilities = scope.Provider.Capabilities;
        bool safeCommits = capabilities.HasFlag(SyncProviderCapabilities.ConditionalWrites)
            || capabilities.HasFlag(SyncProviderCapabilities.ServerCreationOrder);
        if (!safeCommits)
        {
            throw new SyncConfigurationException(
                $"Provider '{scope.Provider.ProviderId}' supports neither conditional writes nor server creation order, so concurrent " +
                "updates could not be detected. It cannot be used by Sta.DataSync.");
        }

        if (mode != SyncMode.UploadOnly && !scope.Source.Capabilities.HasFlag(SyncDataSourceCapabilities.Write))
        {
            throw new SyncConfigurationException($"Data source '{scope.Source.SourceId}' is read-only; use SyncMode.UploadOnly for scope '{scope.ScopeId}'.");
        }

        if (strategy == ConflictResolutionStrategy.Custom && !hasCustomResolver)
        {
            throw new SyncConfigurationException($"Scope '{scope.ScopeId}' uses ConflictResolutionStrategy.Custom but no IConflictResolver is registered.");
        }
    }

    /// <summary>Error message when the source cannot perform the operation, otherwise <see langword="null"/>.</summary>
    public static string? ValidateOperation(PlannedOperation operation, SyncDataSourceCapabilities capabilities) => operation.Kind switch
    {
        SyncOperationKind.Download when !capabilities.HasFlag(SyncDataSourceCapabilities.Write)
            => "The data source cannot write items.",
        SyncOperationKind.UploadConflictCopy when operation.WritesLocalCopy && !capabilities.HasFlag(SyncDataSourceCapabilities.Write)
            => "The data source cannot write conflict copies.",
        SyncOperationKind.DeleteLocal when !capabilities.HasFlag(SyncDataSourceCapabilities.Delete)
            => "The data source cannot delete items.",
        _ => null,
    };
}
