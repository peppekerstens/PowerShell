// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Shared types used by both Service.cs (Windows) and ServiceUnix.cs (Linux).
// This file has no platform guard so it compiles on all targets.

namespace Microsoft.PowerShell.Commands
{
    #region ServiceStartupType

    /// <summary>
    /// Enum for usage with StartupType. Automatic, Manual and Disabled index
    /// matched from <see cref="System.ServiceProcess.ServiceStartMode"/>.
    /// </summary>
    public enum ServiceStartupType
    {
        /// <summary>Invalid service start type.</summary>
        InvalidValue = -1,
        /// <summary>Automatic service.</summary>
        Automatic = 2,
        /// <summary>Manual service.</summary>
        Manual = 3,
        /// <summary>Disabled service.</summary>
        Disabled = 4,
        /// <summary>Automatic (Delayed Start) service.</summary>
        AutomaticDelayedStart = 10
    }

    #endregion ServiceStartupType
}
