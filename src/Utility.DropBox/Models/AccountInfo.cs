using Dropbox.Api.Users;

namespace Utility.DropBox.Models;

/// <summary>Flattened view of the connected Dropbox account, decoupled from the SDK types.</summary>
public sealed record AccountInfo(
    string? AccountId,
    string? Email,
    string? DisplayName,
    string? GivenName,
    string? Surname,
    string? Country,
    string? Locale,
    bool EmailVerified,
    bool Disabled,
    string? ProfilePhotoUrl,
    long UsedBytes,
    long AllocatedBytes,
    string? RootNamespaceId = null,
    string? HomeNamespaceId = null,
    string? TeamName = null)
{
    public double UsedGb => UsedBytes / 1024d / 1024d / 1024d;
    public double AllocatedGb => AllocatedBytes / 1024d / 1024d / 1024d;

    public static AccountInfo From(FullAccount account, SpaceUsage? usage)
    {
        var name = account.Name;
        return new AccountInfo(
            account.AccountId,
            account.Email,
            name?.DisplayName,
            name?.GivenName,
            name?.Surname,
            account.Country,
            account.Locale,
            account.EmailVerified,
            account.Disabled,
            account.ProfilePhotoUrl,
            (long)(usage?.Used ?? 0UL),
            AllocatedOf(usage),
            account.RootInfo?.RootNamespaceId,
            account.RootInfo?.HomeNamespaceId,
            account.Team?.Name);
    }

    /// <summary>
    /// The namespaces this account can browse: always the personal space, plus the team
    /// root when the account is a Business team member. Team content is invisible until a
    /// client is scoped to the team namespace id.
    /// </summary>
    public IReadOnlyList<DropBoxNamespace> GetNamespaces()
    {
        var list = new List<DropBoxNamespace>();

        if (!string.IsNullOrWhiteSpace(HomeNamespaceId))
        {
            list.Add(DropBoxNamespace.Personal(HomeNamespaceId));
        }

        if (!string.IsNullOrWhiteSpace(RootNamespaceId) && RootNamespaceId != HomeNamespaceId)
        {
            list.Add(new DropBoxNamespace(
                RootNamespaceId,
                TeamName is null || TeamName.Length == 0 ? "Team" : TeamName,
                IsPersonal: false,
                TeamName: TeamName));
        }

        return list;
    }

    /// <summary>
    /// SpaceAllocation is a union: the allocated byte count lives on the selected variant.
    /// </summary>
    private static long AllocatedOf(SpaceUsage? usage)
    {
        var allocation = usage?.Allocation;
        if (allocation is null)
        {
            return 0;
        }

        if (allocation.IsIndividual)
        {
            return (long)allocation.AsIndividual.Value.Allocated;
        }

        if (allocation.IsTeam)
        {
            return (long)allocation.AsTeam.Value.Allocated;
        }

        return 0;
    }
}