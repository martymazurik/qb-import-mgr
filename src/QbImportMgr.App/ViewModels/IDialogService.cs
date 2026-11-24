using QbImportMgr.Core.Domain;

namespace QbImportMgr.App.ViewModels;

public interface IDialogService
{
    void Info(string message, string title = "qb-import-mgr");
    void Error(string message, string title = "qb-import-mgr");
    bool Confirm(string message, string title = "qb-import-mgr");

    /// <summary>Two-button question with custom button text; true for the first button.</summary>
    bool Ask(string message, string title, string yesText, string noText);
    string? OpenFile(string title, string filter);
    void ShowText(string title, string text);
    bool ShowAccountMapping(AccountMappingViewModel vm);
    bool ShowRuleEditor(RuleEditorViewModel vm);
    void ShowExport(ExportViewModel vm);
    void ShowBatches(BatchesViewModel vm);
    void OpenFolder(string path);

    /// <summary>Picks one of the given accounts; null when cancelled.</summary>
    long? ChooseAccount(IReadOnlyList<BankAccount> accounts, string prompt);
    bool ShowPickMatch(PickMatchViewModel vm);
    void ShowLearning(LearningViewModel vm);
    void ShowNames(NamesViewModel vm);
    void ShowAudit(AuditViewModel vm);

    /// <summary>Name and kind for a new split template; null when cancelled.</summary>
    (string Name, TemplateLineKind Kind)? AskTemplate();
}
