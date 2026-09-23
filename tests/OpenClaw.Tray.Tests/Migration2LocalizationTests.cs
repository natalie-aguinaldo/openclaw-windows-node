using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

public sealed class Migration2LocalizationTests
{
    [Theory]
    [InlineData("en-us", "protected migration records", "If validation succeeds",
        "blocked from starting normally", "Only after migration completion is recorded",
        "manually uninstall", "only after removal of the previous app is verified",
        "configuration, gateway, files, and models stay in place",
        "nothing is uninstalled automatically", "without starting migration", "close safely")]
    [InlineData("fr-fr", "données de migration protégées", "Si la validation réussit",
        "ne pourra plus démarrer normalement", "Uniquement après l'enregistrement de la fin de la migration",
        "désinstallez manuellement", "qu'après vérification de la désinstallation de l'application précédente",
        "configuration existante, votre passerelle, vos fichiers et vos modèles resteront en place",
        "rien ne sera désinstallé automatiquement", "sans démarrer la migration", "se fermer en toute sécurité")]
    [InlineData("nl-nl", "beveiligde migratiegegevens", "Als de validatie slaagt",
        "niet meer normaal starten", "Pas nadat de voltooiing van de migratie is vastgelegd",
        "handmatig", "nadat de verwijdering van de vorige app is geverifieerd",
        "configuratie, gateway, bestanden en modellen blijven op hun plaats",
        "er wordt niets automatisch verwijderd", "zonder de migratie te starten", "veilig af te sluiten")]
    [InlineData("pt-br", "registros protegidos de migração", "Se a validação for bem-sucedida",
        "não poderá mais iniciar normalmente", "Somente após o registro da conclusão da migração",
        "desinstale manualmente", "só poderá ser usada após a verificação da remoção do aplicativo anterior",
        "configuração existente, gateway, arquivos e modelos permanecerão no mesmo local",
        "nada será desinstalado automaticamente", "sem iniciar a migração", "encerrado com segurança")]
    [InlineData("zh-cn", "受保护的迁移记录", "如果验证成功",
        "将无法正常启动", "只有在迁移完成状态已记录后", "手动卸载",
        "只有在确认旧版应用已移除后，才能使用 Store 版本",
        "现有配置、网关、文件和模型将保留在原处",
        "不会自动卸载任何应用", "不会开始迁移", "安全关闭")]
    [InlineData("zh-tw", "受保護的移轉記錄", "如果驗證成功",
        "將無法正常啟動", "只有在移轉完成狀態已記錄後", "手動解除安裝",
        "只有在確認舊版應用程式已移除後，才能使用 Store 版本",
        "現有設定、閘道、檔案和模型將保留在原處",
        "不會自動解除安裝任何應用程式", "不會開始移轉", "安全關閉")]
    public void ActualConsentSurfaces_DiscloseSafetyBoundariesInEveryLocale(
        string locale, string protectedRecords, string validationCondition,
        string startupBlock, string completionBeforeUninstall, string manualUninstall,
        string verifiedRemoval, string retainedData, string noAutomaticUninstall,
        string cancelWithoutMigration, string gracefulClose)
    {
        var resources = ReadResources(locale);
        foreach (var key in new[] { "Migration2_Consent", "Migration2_InnoConsent" })
        {
            var consent = resources[key];
            foreach (var disclosure in new[]
            {
                protectedRecords, validationCondition, startupBlock, completionBeforeUninstall,
                manualUninstall, verifiedRemoval, retainedData, noAutomaticUninstall,
                cancelWithoutMigration, gracefulClose
            })
                Assert.Contains(disclosure, consent);

            Assert.Contains(resources["Migration_StoreNotNow"], consent);
            Assert.Contains(resources["Migration_StoreRetry"], consent);
            Assert.Contains(resources[key == "Migration2_Consent"
                ? "Migration_StoreMigrate" : "Migration2_InnoAction"], consent);
            foreach (var nativeChoice in new[] { resources["Migration_StoreYes"], resources["Migration_StoreNo"] })
            {
                Assert.DoesNotContain(nativeChoice + ":", consent);
                Assert.DoesNotContain(nativeChoice + "\uFF1A", consent);
            }
            Assert.DoesNotContain("{0}", consent);
            Assert.DoesNotContain("{1}", consent);
            Assert.DoesNotContain("\u2014", consent);
        }

        Assert.Contains("30", resources["Migration2_InnoConsent"]);
        Assert.Contains(verifiedRemoval, resources["Migration2_AwaitingRemoval"]);
        Assert.Contains(retainedData, resources["Migration2_AwaitingRemoval"]);
        Assert.Contains(noAutomaticUninstall, resources["Migration2_AwaitingRemoval"]);
    }

    [Theory]
    [InlineData("en-us", "tray menu", "Do not force-close or uninstall it yet",
        "Uninstall only after this window confirms that migration completion was recorded",
        "verify its removal and finish migration", "Keep the Store version installed",
        "Do not delete migration data or run gateway cleanup")]
    [InlineData("fr-fr", "menu dans la zone de notification",
        "Ne forcez pas sa fermeture et ne la désinstallez pas encore",
        "Désinstallez-la uniquement après que cette fenêtre confirme l'enregistrement de la fin de la migration",
        "vérifier sa désinstallation et terminer la migration", "Gardez la version du Store installée",
        "Ne supprimez pas les données de migration et ne lancez pas le nettoyage de la passerelle")]
    [InlineData("nl-nl", "menu in het systeemvak",
        "Forceer het afsluiten niet en verwijder de app nog niet",
        "Verwijder de app pas nadat dit venster bevestigt dat de voltooiing van de migratie is vastgelegd",
        "de verwijdering te verifiëren en de migratie af te ronden", "Laat de Store-versie geïnstalleerd",
        "Verwijder geen migratiegegevens en voer geen gatewayopruiming uit")]
    [InlineData("pt-br", "menu na bandeja do sistema",
        "Não force o encerramento nem desinstale o aplicativo ainda",
        "Desinstale somente após esta janela confirmar que a conclusão da migração foi registrada",
        "verificar a remoção e finalizar a migração", "Mantenha a versão da Store instalada",
        "Não exclua os dados de migração nem execute a limpeza do gateway")]
    [InlineData("zh-cn", "托盘菜单", "暂时不要强制关闭或卸载该应用",
        "只有在此窗口确认迁移完成状态已记录后，才能卸载",
        "验证移除状态并完成迁移", "请保留已安装的 Store 版本", "请勿删除迁移数据或运行网关清理")]
    [InlineData("zh-tw", "系統匣選單", "暫時不要強制關閉或解除安裝該應用程式",
        "只有在此視窗確認移轉完成狀態已記錄後，才能解除安裝",
        "驗證移除狀態並完成移轉", "請保留已安裝的 Store 版本", "請勿刪除移轉資料或執行閘道清理")]
    public void ManualCloseAndRemoval_UseRetryAndPreserveSafetyGuidance(
        string locale, string trayMenu, string noForcedExit,
        string completionBeforeUninstall, string retryFinalization,
        string keepStore, string noCleanup)
    {
        var resources = ReadResources(locale);
        var close = resources["Migration2_CloseInno"];
        Assert.Contains(trayMenu, close);
        Assert.Contains(noForcedExit, close);
        Assert.Contains(completionBeforeUninstall, close);
        foreach (var key in new[] { "Migration2_CloseInno", "Migration2_AwaitingRemoval" })
        {
            Assert.Contains(resources["Migration_StoreRetry"], resources[key]);
            Assert.Contains(retryFinalization, resources[key]);
            Assert.DoesNotContain("\u2014", resources[key]);
        }
        Assert.Contains(keepStore, resources["Migration2_AwaitingRemoval"]);
        Assert.Contains(noCleanup, resources["Migration2_AwaitingRemoval"]);
    }

    [Fact]
    public void EnglishShippingGuidance_DoesNotRequireReopeningStoreOrUsePreviewCopy()
    {
        var resources = ReadResources("en-us");
        foreach (var key in new[]
        {
            "Migration2_Consent", "Migration2_InnoConsent",
            "Migration2_CloseInno", "Migration2_AwaitingRemoval"
        })
        {
            Assert.DoesNotContain("reopen", resources[key], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("preview", resources[key], StringComparison.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, string> ReadResources(string locale) =>
        XDocument.Load(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Strings", locale, "Resources.resw"))
            .Root!.Elements("data").ToDictionary(
                element => element.Attribute("name")!.Value,
                element => element.Element("value")!.Value,
                StringComparer.Ordinal);
}
