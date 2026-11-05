using UnityEngine;

public static class Scene {

    private static ManageTools _toolManager;
    private static ToolPanelUI _toolPanelUI;
    private static DataSource _dataSource;
    private static Watch _watch;
    private static FilterTool _filterTool;
    private static ProfileTool _profileTool;
    private static SortTool _sortTool;
    private static ManageSheets _sheetManager;
    private static ManageDatasets _manageDatasets;
    private static Tooltip _tooltip;

    public static ManageTools Tools => Resolve(ref _toolManager);
    public static ToolPanelUI ToolPanel => Resolve(ref _toolPanelUI);
    public static Watch Assistant => Resolve(ref _watch);
    public static FilterTool Filter => Resolve(ref _filterTool);
    public static ProfileTool Profile => Resolve(ref _profileTool);
    public static SortTool Sort => Resolve(ref _sortTool);
    public static ManageSheets Sheets => Resolve(ref _sheetManager);
    public static ManageDatasets Datasets => ManageDatasets.Instance != null ? ManageDatasets.Instance : Resolve(ref _manageDatasets);
    public static Tooltip Tooltip => Resolve(ref _tooltip);

    public static string DatasetLabel {
        get {
            var d = Datasets;
            var active = d != null ? d.ActiveDataset : null;
            return active != null ? active.Name : "dataset";
        }
    }

    // The sheet on the table's data. With datasets managed, that is the open
    // one or nothing: before the first sheet opens there is none, and the tools must not
    // read or act on whichever dataset happened to be open last.
    public static DataSource Data {
        get {
            if (ManageDatasets.Instance != null) return ManageDatasets.ActiveSource;
            if (_dataSource == null) {
                var sheets = Sheets;
                _dataSource = sheets != null && sheets.dataSource != null
                    ? sheets.dataSource
                    : Object.FindAnyObjectByType<DataSource>();
            }
            return _dataSource;
        }
    }

    private static T Resolve<T>(ref T cached) where T : Object {
        if (cached == null) cached = Object.FindAnyObjectByType<T>();
        return cached;
    }
}
