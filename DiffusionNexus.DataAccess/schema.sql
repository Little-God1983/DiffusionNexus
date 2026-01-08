CREATE TABLE "AppSettings" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_AppSettings" PRIMARY KEY AUTOINCREMENT,
    "EncryptedCivitaiApiKey" TEXT NULL,
    "ShowNsfw" INTEGER NOT NULL,
    "GenerateVideoThumbnails" INTEGER NOT NULL,
    "ShowVideoPreview" INTEGER NOT NULL,
    "UseForgeStylePrompts" INTEGER NOT NULL,
    "MergeLoraSources" INTEGER NOT NULL,
    "LoraSortSourcePath" TEXT NULL,
    "LoraSortTargetPath" TEXT NULL,
    "DeleteEmptySourceFolders" INTEGER NOT NULL,
    "DatasetStoragePath" TEXT NULL,
    "AutoBackupEnabled" INTEGER NOT NULL,
    "AutoBackupIntervalDays" INTEGER NOT NULL,
    "AutoBackupIntervalHours" INTEGER NOT NULL,
    "AutoBackupLocation" TEXT NULL,
    "LastBackupAt" TEXT NULL,
    "MaxBackups" INTEGER NOT NULL,
    "UpdatedAt" TEXT NOT NULL
);


CREATE TABLE "Creators" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Creators" PRIMARY KEY AUTOINCREMENT,
    "Username" TEXT NOT NULL,
    "AvatarUrl" TEXT NULL,
    "CreatedAt" TEXT NOT NULL
);


CREATE TABLE "DisclaimerAcceptances" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_DisclaimerAcceptances" PRIMARY KEY AUTOINCREMENT,
    "WindowsUsername" TEXT NOT NULL,
    "AcceptedAt" TEXT NOT NULL,
    "Accepted" INTEGER NOT NULL
);


CREATE TABLE "Tags" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Tags" PRIMARY KEY AUTOINCREMENT,
    "Name" TEXT NOT NULL,
    "NormalizedName" TEXT NOT NULL
);


CREATE TABLE "DatasetCategories" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_DatasetCategories" PRIMARY KEY AUTOINCREMENT,
    "Name" TEXT NOT NULL,
    "Description" TEXT NULL,
    "Order" INTEGER NOT NULL,
    "IsDefault" INTEGER NOT NULL,
    "AppSettingsId" INTEGER NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    CONSTRAINT "FK_DatasetCategories_AppSettings_AppSettingsId" FOREIGN KEY ("AppSettingsId") REFERENCES "AppSettings" ("Id") ON DELETE CASCADE
);


CREATE TABLE "LoraSources" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_LoraSources" PRIMARY KEY AUTOINCREMENT,
    "AppSettingsId" INTEGER NOT NULL,
    "FolderPath" TEXT NOT NULL,
    "IsEnabled" INTEGER NOT NULL,
    "Order" INTEGER NOT NULL,
    CONSTRAINT "FK_LoraSources_AppSettings_AppSettingsId" FOREIGN KEY ("AppSettingsId") REFERENCES "AppSettings" ("Id") ON DELETE CASCADE
);


CREATE TABLE "Models" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Models" PRIMARY KEY AUTOINCREMENT,
    "CivitaiId" INTEGER NULL,
    "Name" TEXT NOT NULL,
    "Description" TEXT NULL,
    "Type" TEXT NOT NULL,
    "IsNsfw" INTEGER NOT NULL,
    "IsPoi" INTEGER NOT NULL,
    "Mode" TEXT NOT NULL,
    "Source" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "LastSyncedAt" TEXT NULL,
    "AllowNoCredit" INTEGER NOT NULL,
    "AllowCommercialUse" TEXT NOT NULL,
    "AllowDerivatives" INTEGER NOT NULL,
    "AllowDifferentLicense" INTEGER NOT NULL,
    "CreatorId" INTEGER NULL,
    CONSTRAINT "FK_Models_Creators_CreatorId" FOREIGN KEY ("CreatorId") REFERENCES "Creators" ("Id") ON DELETE SET NULL
);


CREATE TABLE "ModelTags" (
    "ModelId" INTEGER NOT NULL,
    "TagId" INTEGER NOT NULL,
    CONSTRAINT "PK_ModelTags" PRIMARY KEY ("ModelId", "TagId"),
    CONSTRAINT "FK_ModelTags_Models_ModelId" FOREIGN KEY ("ModelId") REFERENCES "Models" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_ModelTags_Tags_TagId" FOREIGN KEY ("TagId") REFERENCES "Tags" ("Id") ON DELETE CASCADE
);


CREATE TABLE "ModelVersions" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ModelVersions" PRIMARY KEY AUTOINCREMENT,
    "CivitaiId" INTEGER NULL,
    "ModelId" INTEGER NOT NULL,
    "Name" TEXT NOT NULL,
    "Description" TEXT NULL,
    "BaseModel" TEXT NOT NULL,
    "BaseModelRaw" TEXT NULL,
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NULL,
    "PublishedAt" TEXT NULL,
    "DownloadUrl" TEXT NULL,
    "EarlyAccessDays" INTEGER NOT NULL,
    "DownloadCount" INTEGER NOT NULL,
    "RatingCount" INTEGER NOT NULL,
    "Rating" REAL NOT NULL,
    "ThumbsUpCount" INTEGER NOT NULL,
    "ThumbsDownCount" INTEGER NOT NULL,
    CONSTRAINT "FK_ModelVersions_Models_ModelId" FOREIGN KEY ("ModelId") REFERENCES "Models" ("Id") ON DELETE CASCADE
);


CREATE TABLE "ModelFiles" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ModelFiles" PRIMARY KEY AUTOINCREMENT,
    "CivitaiId" INTEGER NULL,
    "ModelVersionId" INTEGER NOT NULL,
    "FileName" TEXT NOT NULL,
    "SizeKB" REAL NOT NULL,
    "FileSizeBytes" INTEGER NULL,
    "FileType" TEXT NOT NULL,
    "IsPrimary" INTEGER NOT NULL,
    "Format" TEXT NOT NULL,
    "Precision" TEXT NOT NULL,
    "SizeType" TEXT NOT NULL,
    "DownloadUrl" TEXT NULL,
    "PickleScanResult" TEXT NOT NULL,
    "PickleScanMessage" TEXT NULL,
    "VirusScanResult" TEXT NOT NULL,
    "ScannedAt" TEXT NULL,
    "HashAutoV1" TEXT NULL,
    "HashAutoV2" TEXT NULL,
    "HashSHA256" TEXT NULL,
    "HashCRC32" TEXT NULL,
    "HashBLAKE3" TEXT NULL,
    "LocalPath" TEXT NULL,
    "IsLocalFileValid" INTEGER NOT NULL,
    "LocalFileVerifiedAt" TEXT NULL,
    CONSTRAINT "FK_ModelFiles_ModelVersions_ModelVersionId" FOREIGN KEY ("ModelVersionId") REFERENCES "ModelVersions" ("Id") ON DELETE CASCADE
);


CREATE TABLE "ModelImages" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ModelImages" PRIMARY KEY AUTOINCREMENT,
    "CivitaiId" INTEGER NULL,
    "ModelVersionId" INTEGER NOT NULL,
    "Url" TEXT NOT NULL,
    "IsNsfw" INTEGER NOT NULL,
    "NsfwLevel" TEXT NOT NULL,
    "Width" INTEGER NOT NULL,
    "Height" INTEGER NOT NULL,
    "BlurHash" TEXT NULL,
    "SortOrder" INTEGER NOT NULL,
    "CreatedAt" TEXT NULL,
    "PostId" INTEGER NULL,
    "Username" TEXT NULL,
    "ThumbnailData" BLOB NULL,
    "ThumbnailMimeType" TEXT NULL,
    "ThumbnailWidth" INTEGER NULL,
    "ThumbnailHeight" INTEGER NULL,
    "LocalCachePath" TEXT NULL,
    "IsLocalCacheValid" INTEGER NOT NULL,
    "CachedAt" TEXT NULL,
    "CachedFileSize" INTEGER NULL,
    "Prompt" TEXT NULL,
    "NegativePrompt" TEXT NULL,
    "Seed" INTEGER NULL,
    "Steps" INTEGER NULL,
    "Sampler" TEXT NULL,
    "CfgScale" REAL NULL,
    "GenerationModel" TEXT NULL,
    "DenoisingStrength" REAL NULL,
    "LikeCount" INTEGER NOT NULL,
    "HeartCount" INTEGER NOT NULL,
    "CommentCount" INTEGER NOT NULL,
    CONSTRAINT "FK_ModelImages_ModelVersions_ModelVersionId" FOREIGN KEY ("ModelVersionId") REFERENCES "ModelVersions" ("Id") ON DELETE CASCADE
);


CREATE TABLE "TriggerWords" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_TriggerWords" PRIMARY KEY AUTOINCREMENT,
    "ModelVersionId" INTEGER NOT NULL,
    "Word" TEXT NOT NULL,
    "Order" INTEGER NOT NULL,
    CONSTRAINT "FK_TriggerWords_ModelVersions_ModelVersionId" FOREIGN KEY ("ModelVersionId") REFERENCES "ModelVersions" ("Id") ON DELETE CASCADE
);


CREATE UNIQUE INDEX "IX_Creators_Username" ON "Creators" ("Username");


CREATE INDEX "IX_DatasetCategories_AppSettingsId" ON "DatasetCategories" ("AppSettingsId");


CREATE INDEX "IX_DatasetCategories_Name" ON "DatasetCategories" ("Name");


CREATE INDEX "IX_DisclaimerAcceptances_WindowsUsername" ON "DisclaimerAcceptances" ("WindowsUsername");


CREATE INDEX "IX_LoraSources_AppSettingsId" ON "LoraSources" ("AppSettingsId");


CREATE INDEX "IX_LoraSources_FolderPath" ON "LoraSources" ("FolderPath");


CREATE INDEX "IX_ModelFiles_CivitaiId" ON "ModelFiles" ("CivitaiId");


CREATE INDEX "IX_ModelFiles_FileSizeBytes" ON "ModelFiles" ("FileSizeBytes");


CREATE INDEX "IX_ModelFiles_HashSHA256" ON "ModelFiles" ("HashSHA256");


CREATE INDEX "IX_ModelFiles_LocalPath" ON "ModelFiles" ("LocalPath");


CREATE INDEX "IX_ModelFiles_ModelVersionId" ON "ModelFiles" ("ModelVersionId");


CREATE INDEX "IX_ModelImages_CivitaiId" ON "ModelImages" ("CivitaiId");


CREATE INDEX "IX_ModelImages_ModelVersionId" ON "ModelImages" ("ModelVersionId");


CREATE INDEX "IX_ModelImages_ModelVersionId_SortOrder" ON "ModelImages" ("ModelVersionId", "SortOrder");


CREATE UNIQUE INDEX "IX_Models_CivitaiId" ON "Models" ("CivitaiId") WHERE [CivitaiId] IS NOT NULL;


CREATE INDEX "IX_Models_CreatedAt" ON "Models" ("CreatedAt");


CREATE INDEX "IX_Models_CreatorId" ON "Models" ("CreatorId");


CREATE INDEX "IX_Models_Name" ON "Models" ("Name");


CREATE INDEX "IX_Models_Type" ON "Models" ("Type");


CREATE INDEX "IX_ModelTags_TagId" ON "ModelTags" ("TagId");


CREATE INDEX "IX_ModelVersions_BaseModel" ON "ModelVersions" ("BaseModel");


CREATE UNIQUE INDEX "IX_ModelVersions_CivitaiId" ON "ModelVersions" ("CivitaiId") WHERE [CivitaiId] IS NOT NULL;


CREATE INDEX "IX_ModelVersions_CreatedAt" ON "ModelVersions" ("CreatedAt");


CREATE INDEX "IX_ModelVersions_ModelId" ON "ModelVersions" ("ModelId");


CREATE UNIQUE INDEX "IX_Tags_NormalizedName" ON "Tags" ("NormalizedName");


CREATE INDEX "IX_TriggerWords_ModelVersionId" ON "TriggerWords" ("ModelVersionId");


