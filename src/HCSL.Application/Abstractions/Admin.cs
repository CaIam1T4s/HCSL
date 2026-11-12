using HCSL.Core;
namespace HCSL.Application.Abstractions;

/// <summary>
/// Framework admin lists (currently CounterStrikeSharp). Reads and edits the framework's own file with a
/// minimal text edit; reload happens in the daemon through the normal console path.
/// </summary>
public interface IAdminStore
{
    AdminResult Execute(WorkspaceRequest workspace, AdminCommand command);
}
