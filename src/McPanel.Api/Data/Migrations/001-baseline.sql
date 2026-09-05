CREATE TABLE "Admins" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Admins" PRIMARY KEY AUTOINCREMENT,
    "Username" TEXT NOT NULL,
    "PasswordHash" TEXT NOT NULL,
    "SessionStamp" TEXT NOT NULL,
    "LastConsoleSequence" INTEGER NOT NULL,
    "CreatedAt" INTEGER NOT NULL
);


CREATE TABLE "Backups" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Backups" PRIMARY KEY,
    "ServerId" TEXT NOT NULL,
    "FileName" TEXT NOT NULL,
    "Size" INTEGER NOT NULL,
    "CreatedAt" INTEGER NOT NULL,
    "Reason" TEXT NOT NULL,
    "State" TEXT NOT NULL,
    "SoftwareMetadataJson" TEXT NULL
);


CREATE TABLE "GateBackends" (
    "GateServerId" TEXT NOT NULL,
    "BackendServerId" TEXT NOT NULL,
    CONSTRAINT "PK_GateBackends" PRIMARY KEY ("GateServerId", "BackendServerId")
);


CREATE TABLE "GateExternalBackends" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_GateExternalBackends" PRIMARY KEY,
    "GateServerId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Host" TEXT NOT NULL,
    "Port" INTEGER NOT NULL
);


CREATE TABLE "GateSettings" (
    "ServerId" TEXT NOT NULL CONSTRAINT "PK_GateSettings" PRIMARY KEY,
    "Mode" TEXT NOT NULL,
    "DefaultBackendServerId" TEXT NULL,
    "DefaultExternalBackendId" TEXT NULL,
    "ClassicForwardingMode" TEXT NOT NULL,
    "ClassicConfigJson" TEXT NULL,
    "ApiPort" INTEGER NOT NULL,
    "Revision" TEXT NOT NULL,
    "ConfigurationDirty" INTEGER NOT NULL,
    "LastApplyError" TEXT NULL,
    "UpdatedAt" INTEGER NOT NULL
);


CREATE TABLE "JavaRuntimes" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_JavaRuntimes" PRIMARY KEY,
    "Path" TEXT NOT NULL,
    "Version" TEXT NOT NULL,
    "Major" INTEGER NOT NULL,
    "Vendor" TEXT NOT NULL,
    "Architecture" TEXT NOT NULL,
    "IsCustom" INTEGER NOT NULL,
    "LastSeenAt" INTEGER NOT NULL
);


CREATE TABLE "Jobs" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Jobs" PRIMARY KEY,
    "Type" TEXT NOT NULL,
    "ServerId" TEXT NULL,
    "ClientRequestId" TEXT NULL,
    "State" TEXT NOT NULL,
    "Progress" INTEGER NOT NULL,
    "Message" TEXT NULL,
    "Error" TEXT NULL,
    "CreatedAt" INTEGER NOT NULL,
    "UpdatedAt" INTEGER NOT NULL
);


CREATE TABLE "PanelSettings" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_PanelSettings" PRIMARY KEY AUTOINCREMENT,
    "KeepServersRunningOnPanelStop" INTEGER NOT NULL,
    "GlobalServerHost" TEXT NULL,
    "Revision" TEXT NOT NULL,
    "UpdatedAt" INTEGER NOT NULL
);


CREATE TABLE "Players" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Players" PRIMARY KEY AUTOINCREMENT,
    "ServerId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Uuid" TEXT NULL,
    "Online" INTEGER NOT NULL,
    "Whitelisted" INTEGER NOT NULL,
    "Operator" INTEGER NOT NULL,
    "Banned" INTEGER NOT NULL,
    "LastSeenAt" INTEGER NOT NULL
);


CREATE TABLE "Schedules" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Schedules" PRIMARY KEY,
    "ServerId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Frequency" TEXT NOT NULL,
    "TimeZone" TEXT NOT NULL,
    "Enabled" INTEGER NOT NULL,
    "TriggerJson" TEXT NOT NULL,
    "ActionsJson" TEXT NOT NULL,
    "NextRunAt" INTEGER NULL,
    "LastRunAt" INTEGER NULL,
    "LastResult" TEXT NULL,
    "IsRunning" INTEGER NOT NULL
);


CREATE TABLE "Servers" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Servers" PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "Kind" TEXT NOT NULL,
    "Version" TEXT NOT NULL,
    "DistributionBuild" TEXT NULL,
    "LoaderVersion" TEXT NULL,
    "InstallerVersion" TEXT NULL,
    "LaunchMode" TEXT NOT NULL,
    "LaunchTarget" TEXT NOT NULL,
    "RequiredJavaMajor" INTEGER NOT NULL,
    "IsExperimental" INTEGER NOT NULL,
    "State" TEXT NOT NULL,
    "Port" INTEGER NOT NULL,
    "PublicHost" TEXT COLLATE NOCASE NULL,
    "PublicPort" INTEGER NULL,
    "AddressRevision" TEXT NOT NULL,
    "MemoryMb" INTEGER NOT NULL,
    "InitialMemoryMb" INTEGER NOT NULL,
    "MemoryLimitMb" INTEGER NOT NULL,
    "JavaRuntimeId" TEXT NOT NULL,
    "JvmArguments" TEXT NOT NULL,
    "UseAikarFlags" INTEGER NOT NULL,
    "StartOnBoot" INTEGER NOT NULL,
    "CrashRecovery" INTEGER NOT NULL,
    "IconRevision" TEXT NULL,
    "ModpackName" TEXT NULL,
    "ModpackVersion" TEXT NULL,
    "ModrinthProjectId" TEXT NULL,
    "ModrinthVersionId" TEXT NULL,
    "ModpackSource" TEXT NULL,
    "EulaAcceptedAt" INTEGER NOT NULL,
    "RestartRequired" INTEGER NOT NULL,
    "CrashAttempts" INTEGER NOT NULL,
    "ProcessId" INTEGER NULL,
    "StartedAt" INTEGER NULL,
    "CreatedAt" INTEGER NOT NULL,
    "UpdatedAt" INTEGER NOT NULL
);


CREATE INDEX "IX_Backups_ServerId_CreatedAt" ON "Backups" ("ServerId", "CreatedAt");


CREATE INDEX "IX_GateBackends_BackendServerId" ON "GateBackends" ("BackendServerId");


CREATE UNIQUE INDEX "IX_GateExternalBackends_GateServerId_Host_Port" ON "GateExternalBackends" ("GateServerId", "Host", "Port");


CREATE UNIQUE INDEX "IX_Jobs_ClientRequestId" ON "Jobs" ("ClientRequestId") WHERE "ClientRequestId" IS NOT NULL;


CREATE UNIQUE INDEX "IX_Players_ServerId_Name" ON "Players" ("ServerId", "Name");


CREATE INDEX "IX_Schedules_Enabled_NextRunAt" ON "Schedules" ("Enabled", "NextRunAt");


