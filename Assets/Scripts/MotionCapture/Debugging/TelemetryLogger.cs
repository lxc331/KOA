using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using UnityEngine;

/// <summary>
/// V59 中文 Excel 遥测记录器。
///
/// 记录流程：
/// 1. 人物开始驱动时自动开始记录，仅把数据写入内存，不在运行过程中进行磁盘 I/O；
/// 2. 传感器 1～传感器 9 分别缓存到独立列表；
/// 3. 点击“停止记录”、断开连接或退出程序时，一次性生成一个 .xlsx；
/// 4. 工作簿包含 9 个中文工作表：传感器1-左上臂 ～ 传感器9-右小腿；
/// 5. 左右前臂表增加肘关节屈曲角；左右小腿表增加膝屈曲角和大小腿几何夹角。
///
/// 采用标准 Open XML 写入，不依赖 Office，也不需要 Unity 额外安装 DLL。
/// </summary>
public class TelemetryLogger : IDisposable
{
    private const int DeviceCount = 9;
    private const string ContentTypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string PackageRelationshipsNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string OfficeRelationshipsNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string ExtendedPropertiesNs = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";
    private const string VTypesNs = "http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes";
    private const int ExcelMaxDataRows = 1048575; // Excel 总行数 1048576，减去表头。
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly string[] ChineseSheetNames =
    {
        "传感器1-左上臂",
        "传感器2-左前臂",
        "传感器3-右上臂",
        "传感器4-右前臂",
        "传感器5-躯干",
        "传感器6-左大腿",
        "传感器7-左小腿",
        "传感器8-右大腿",
        "传感器9-右小腿"
    };

    private sealed class TelemetryRow
    {
        public string Timestamp;
        public long Frame;
        public int DeviceId;
        public byte ProtocolVersion;
        public uint HardwareId;
        public uint SourceSequence;
        public uint SenderTickMs;
        public long SourceLostFrames;
        public long SourceDuplicateFrames;
        public long SourceOutOfOrderFrames;
        public long DuplicateLogicalIdFrames;
        public float ReceiveFrameRateHz;
        public float SourceReportedFrameRateHz;
        public float SourceDeliveryPercent;
        public float Q0;
        public float Q1;
        public float Q2;
        public float Q3;
        public float Yaw;
        public float Pitch;
        public float Roll;
        public float ElbowFlexionDeg;
        public float KneeFlexionDeg;
        public float KneeIncludedDeg;
    }

    public bool IsLogging { get; private set; }
    public string CurrentLogPath { get; private set; } = string.Empty;

    // 兼容旧 UI/Controller 字段。是否记录由开始/停止按钮控制。
    public bool SaveEnabled { get; set; } = true;

    private string exportDirectory;
    private readonly List<TelemetryRow>[] sensorRows = new List<TelemetryRow>[DeviceCount];
    private readonly long[] frameCounters = new long[DeviceCount];
    private bool warnedRowLimit;

    public TelemetryLogger(string exportDirectory)
    {
        this.exportDirectory = string.IsNullOrEmpty(exportDirectory)
            ? Directory.GetCurrentDirectory()
            : exportDirectory;

        for (int i = 0; i < DeviceCount; i++)
            sensorRows[i] = new List<TelemetryRow>(4096);
    }

    public void SetExportDirectory(string dir)
    {
        if (IsLogging)
        {
            Debug.LogWarning("[TelemetryLogger] 正在记录时不能更改导出目录，请先停止记录。");
            return;
        }
        exportDirectory = string.IsNullOrEmpty(dir) ? Directory.GetCurrentDirectory() : dir;
    }

    public string GetExportDirectory() => exportDirectory;

    /// <summary>开始内存记录。不会立即创建磁盘文件。</summary>
    public bool Open()
    {
        if (IsLogging) return true;
        if (!SaveEnabled) return false;

        try
        {
            string dir = string.IsNullOrEmpty(exportDirectory)
                ? Directory.GetCurrentDirectory()
                : exportDirectory;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            ClearBuffers();
            CurrentLogPath = BuildNextDailyLogPath(dir);
            IsLogging = true;
            Debug.Log($"[TelemetryLogger] 开始内存记录，停止后生成 Excel: {CurrentLogPath}");
            return true;
        }
        catch (Exception ex)
        {
            IsLogging = false;
            CurrentLogPath = string.Empty;
            Debug.LogError($"[TelemetryLogger] 开始记录失败: {ex.Message}");
            return false;
        }
    }

    private static string BuildNextDailyLogPath(string directory)
    {
        string dateText = DateTime.Now.ToString("yyyyMMdd", Invariant);
        int dataNumber = 1;

        while (true)
        {
            string fileName = $"{dateText}_动捕数据_{dataNumber}.xlsx";
            string candidate = Path.Combine(directory, fileName);
            if (!File.Exists(candidate) && !File.Exists(candidate + ".tmp"))
                return candidate;
            dataNumber++;
        }
    }

    public void LogFrame(
        int deviceId,
        Quaternion q,
        Vector3 euler,
        byte protocolVersion,
        uint hardwareId,
        uint sourceSequence,
        uint senderTickMs,
        long sourceLostFrames,
        long sourceDuplicateFrames,
        long sourceOutOfOrderFrames,
        long duplicateLogicalIdFrames,
        float receiveFrameRateHz,
        float sourceReportedFrameRateHz,
        float sourceDeliveryPercent,
        float leftElbowFlexionDeg,
        float rightElbowFlexionDeg,
        float leftKneeFlexionDeg,
        float leftKneeIncludedDeg,
        float rightKneeFlexionDeg,
        float rightKneeIncludedDeg)
    {
        if (!IsLogging || !SaveEnabled) return;
        if (deviceId < 0 || deviceId >= DeviceCount) return;

        List<TelemetryRow> rows = sensorRows[deviceId];
        if (rows.Count >= ExcelMaxDataRows)
        {
            if (!warnedRowLimit)
            {
                warnedRowLimit = true;
                Debug.LogWarning("[TelemetryLogger] Excel 单工作表已达到最大行数，后续数据停止追加。");
            }
            return;
        }

        float elbow = float.NaN;
        if (deviceId == (int)BoneIndex.LeftForeArm)
            elbow = leftElbowFlexionDeg;
        else if (deviceId == (int)BoneIndex.RightForeArm)
            elbow = rightElbowFlexionDeg;

        float kneeFlexion = float.NaN;
        float kneeIncluded = float.NaN;
        if (deviceId == (int)BoneIndex.LeftLeg)
        {
            kneeFlexion = leftKneeFlexionDeg;
            kneeIncluded = leftKneeIncludedDeg;
        }
        else if (deviceId == (int)BoneIndex.RightLeg)
        {
            kneeFlexion = rightKneeFlexionDeg;
            kneeIncluded = rightKneeIncludedDeg;
        }

        rows.Add(new TelemetryRow
        {
            Timestamp = DateTime.Now.ToString("O", Invariant),
            Frame = ++frameCounters[deviceId],
            DeviceId = deviceId,
            ProtocolVersion = protocolVersion,
            HardwareId = hardwareId,
            SourceSequence = sourceSequence,
            SenderTickMs = senderTickMs,
            SourceLostFrames = sourceLostFrames,
            SourceDuplicateFrames = sourceDuplicateFrames,
            SourceOutOfOrderFrames = sourceOutOfOrderFrames,
            DuplicateLogicalIdFrames = duplicateLogicalIdFrames,
            ReceiveFrameRateHz = receiveFrameRateHz,
            SourceReportedFrameRateHz = sourceReportedFrameRateHz,
            SourceDeliveryPercent = sourceDeliveryPercent,
            Q0 = q.x,
            Q1 = q.y,
            Q2 = q.z,
            Q3 = q.w,
            // 保持旧版轴定义：yaw=euler.z, pitch=euler.y, roll=euler.x。
            // Unity 的 eulerAngles 通常为 0~360°，Excel 统一转换为更直观的 -180~180°。
            Yaw = NormalizeSignedAngle(euler.z),
            Pitch = NormalizeSignedAngle(euler.y),
            Roll = NormalizeSignedAngle(euler.x),
            ElbowFlexionDeg = elbow,
            KneeFlexionDeg = kneeFlexion,
            KneeIncludedDeg = kneeIncluded
        });
    }


    private static float NormalizeSignedAngle(float angle)
    {
        angle %= 360f;
        if (angle > 180f) angle -= 360f;
        if (angle <= -180f) angle += 360f;
        return angle;
    }

    /// <summary>
    /// 兼容旧 Controller。运行时不写磁盘，因此无需周期刷新。
    /// </summary>
    public void FlushIfDue() { }

    /// <summary>
    /// 兼容旧 Controller。断开连接时由 Controller 主动 Close，因此此方法不自动开始记录。
    /// </summary>
    public void SyncState(bool isConnected)
    {
        if (!isConnected && IsLogging)
            Close();
    }

    /// <summary>停止记录并一次性生成 .xlsx。</summary>
    public void Close()
    {
        if (!IsLogging) return;

        string path = CurrentLogPath;
        IsLogging = false;

        try
        {
            WriteWorkbook(path);
            Debug.Log($"[TelemetryLogger] Excel 已保存: {path}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TelemetryLogger] Excel 保存失败: {ex}");
        }
        finally
        {
            ClearBuffers();
            CurrentLogPath = string.Empty;
        }
    }

    public void Dispose() => Close();

    private void ClearBuffers()
    {
        for (int i = 0; i < DeviceCount; i++)
        {
            sensorRows[i].Clear();
            frameCounters[i] = 0;
        }
        warnedRowLimit = false;
    }

    private void WriteWorkbook(string targetPath)
    {
        if (string.IsNullOrEmpty(targetPath))
            throw new InvalidOperationException("Excel 输出路径为空。");

        string directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        string tempPath = targetPath + ".tmp";
        if (File.Exists(tempPath)) File.Delete(tempPath);

        using (FileStream file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        using (ZipArchive archive = new ZipArchive(file, ZipArchiveMode.Create, false))
        {
            WriteContentTypes(archive);
            WriteRootRelationships(archive);
            WriteCoreProperties(archive);
            WriteAppProperties(archive);
            WriteWorkbookXml(archive);
            WriteWorkbookRelationships(archive);
            WriteStyles(archive);

            for (int i = 0; i < DeviceCount; i++)
                WriteWorksheet(archive, i, sensorRows[i]);
        }

        if (File.Exists(targetPath)) File.Delete(targetPath);
        File.Move(tempPath, targetPath);
    }

    private static XmlWriter CreateXmlWriter(Stream stream)
    {
        return XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            CloseOutput = false,
            OmitXmlDeclaration = false
        });
    }

    private static Stream CreateEntry(ZipArchive archive, string path)
    {
        return archive.CreateEntry(path, System.IO.Compression.CompressionLevel.Fastest).Open();
    }

    private static void WriteContentTypes(ZipArchive archive)
    {
        using (Stream stream = CreateEntry(archive, "[Content_Types].xml"))
        using (XmlWriter xw = CreateXmlWriter(stream))
        {
            xw.WriteStartDocument(true);
            xw.WriteStartElement("Types", ContentTypesNs);
            WriteTypeDefault(xw, "rels", "application/vnd.openxmlformats-package.relationships+xml");
            WriteTypeDefault(xw, "xml", "application/xml");
            WriteTypeOverride(xw, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
            WriteTypeOverride(xw, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
            WriteTypeOverride(xw, "/docProps/core.xml", "application/vnd.openxmlformats-package.core-properties+xml");
            WriteTypeOverride(xw, "/docProps/app.xml", "application/vnd.openxmlformats-officedocument.extended-properties+xml");
            for (int i = 1; i <= DeviceCount; i++)
                WriteTypeOverride(xw, "/xl/worksheets/sheet" + i + ".xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
            xw.WriteEndElement();
            xw.WriteEndDocument();
        }
    }

    private static void WriteTypeDefault(XmlWriter xw, string extension, string contentType)
    {
        xw.WriteStartElement("Default", ContentTypesNs);
        xw.WriteAttributeString("Extension", extension);
        xw.WriteAttributeString("ContentType", contentType);
        xw.WriteEndElement();
    }

    private static void WriteTypeOverride(XmlWriter xw, string partName, string contentType)
    {
        xw.WriteStartElement("Override", ContentTypesNs);
        xw.WriteAttributeString("PartName", partName);
        xw.WriteAttributeString("ContentType", contentType);
        xw.WriteEndElement();
    }

    private static void WriteRootRelationships(ZipArchive archive)
    {
        using (Stream stream = CreateEntry(archive, "_rels/.rels"))
        using (XmlWriter xw = CreateXmlWriter(stream))
        {
            xw.WriteStartDocument(true);
            xw.WriteStartElement("Relationships", PackageRelationshipsNs);
            WriteRelationship(xw, "rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml");
            WriteRelationship(xw, "rId2", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties", "docProps/core.xml");
            WriteRelationship(xw, "rId3", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties", "docProps/app.xml");
            xw.WriteEndElement();
            xw.WriteEndDocument();
        }
    }

    private static void WriteRelationship(XmlWriter xw, string id, string type, string target)
    {
        xw.WriteStartElement("Relationship", PackageRelationshipsNs);
        xw.WriteAttributeString("Id", id);
        xw.WriteAttributeString("Type", type);
        xw.WriteAttributeString("Target", target);
        xw.WriteEndElement();
    }

    private static void WriteCoreProperties(ZipArchive archive)
    {
        using (Stream stream = CreateEntry(archive, "docProps/core.xml"))
        using (XmlWriter xw = CreateXmlWriter(stream))
        {
            xw.WriteStartDocument(true);
            xw.WriteStartElement("cp", "coreProperties", "http://schemas.openxmlformats.org/package/2006/metadata/core-properties");
            xw.WriteAttributeString("xmlns", "dc", null, "http://purl.org/dc/elements/1.1/");
            xw.WriteAttributeString("xmlns", "dcterms", null, "http://purl.org/dc/terms/");
            xw.WriteAttributeString("xmlns", "dcmitype", null, "http://purl.org/dc/dcmitype/");
            xw.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
            xw.WriteElementString("dc", "creator", "http://purl.org/dc/elements/1.1/", "Unity 动作捕捉系统 V59");
            xw.WriteElementString("dc", "title", "http://purl.org/dc/elements/1.1/", "动作捕捉传感器数据");
            xw.WriteStartElement("dcterms", "created", "http://purl.org/dc/terms/");
            xw.WriteAttributeString("xsi", "type", "http://www.w3.org/2001/XMLSchema-instance", "dcterms:W3CDTF");
            xw.WriteString(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", Invariant));
            xw.WriteEndElement();
            xw.WriteEndElement();
            xw.WriteEndDocument();
        }
    }

    private static void WriteAppProperties(ZipArchive archive)
    {
        using (Stream stream = CreateEntry(archive, "docProps/app.xml"))
        using (XmlWriter xw = CreateXmlWriter(stream))
        {
            xw.WriteStartDocument(true);
            xw.WriteStartElement("Properties", ExtendedPropertiesNs);
            xw.WriteAttributeString("xmlns", "vt", null, VTypesNs);
            xw.WriteElementString("Application", ExtendedPropertiesNs, "Unity 动作捕捉系统 V59");
            xw.WriteElementString("DocSecurity", ExtendedPropertiesNs, "0");
            xw.WriteElementString("ScaleCrop", ExtendedPropertiesNs, "false");
            xw.WriteStartElement("HeadingPairs", ExtendedPropertiesNs);
            xw.WriteStartElement("vt", "vector", VTypesNs);
            xw.WriteAttributeString("size", "2");
            xw.WriteAttributeString("baseType", "variant");
            xw.WriteStartElement("vt", "variant", VTypesNs);
            xw.WriteElementString("vt", "lpstr", VTypesNs, "工作表");
            xw.WriteEndElement();
            xw.WriteStartElement("vt", "variant", VTypesNs);
            xw.WriteElementString("vt", "i4", VTypesNs, DeviceCount.ToString(Invariant));
            xw.WriteEndElement();
            xw.WriteEndElement();
            xw.WriteEndElement();
            xw.WriteStartElement("TitlesOfParts", ExtendedPropertiesNs);
            xw.WriteStartElement("vt", "vector", VTypesNs);
            xw.WriteAttributeString("size", DeviceCount.ToString(Invariant));
            xw.WriteAttributeString("baseType", "lpstr");
            for (int i = 0; i < DeviceCount; i++)
                xw.WriteElementString("vt", "lpstr", VTypesNs, ChineseSheetNames[i]);
            xw.WriteEndElement();
            xw.WriteEndElement();
            xw.WriteElementString("Company", ExtendedPropertiesNs, string.Empty);
            xw.WriteElementString("LinksUpToDate", ExtendedPropertiesNs, "false");
            xw.WriteElementString("SharedDoc", ExtendedPropertiesNs, "false");
            xw.WriteElementString("HyperlinksChanged", ExtendedPropertiesNs, "false");
            xw.WriteElementString("AppVersion", ExtendedPropertiesNs, "1.0");
            xw.WriteEndElement();
            xw.WriteEndDocument();
        }
    }

    private static void WriteWorkbookXml(ZipArchive archive)
    {
        using (Stream stream = CreateEntry(archive, "xl/workbook.xml"))
        using (XmlWriter xw = CreateXmlWriter(stream))
        {
            xw.WriteStartDocument(true);
            xw.WriteStartElement("workbook", SpreadsheetNs);
            xw.WriteAttributeString("xmlns", "r", null, OfficeRelationshipsNs);
            xw.WriteStartElement("bookViews", SpreadsheetNs);
            xw.WriteStartElement("workbookView", SpreadsheetNs);
            xw.WriteAttributeString("xWindow", "0");
            xw.WriteAttributeString("yWindow", "0");
            xw.WriteAttributeString("windowWidth", "24000");
            xw.WriteAttributeString("windowHeight", "12000");
            xw.WriteEndElement();
            xw.WriteEndElement();
            xw.WriteStartElement("sheets", SpreadsheetNs);
            for (int i = 0; i < DeviceCount; i++)
            {
                xw.WriteStartElement("sheet", SpreadsheetNs);
                xw.WriteAttributeString("name", ChineseSheetNames[i]);
                xw.WriteAttributeString("sheetId", (i + 1).ToString(Invariant));
                xw.WriteAttributeString("r", "id", OfficeRelationshipsNs, "rId" + (i + 1));
                xw.WriteEndElement();
            }
            xw.WriteEndElement();
            xw.WriteStartElement("calcPr", SpreadsheetNs);
            xw.WriteAttributeString("calcId", "191029");
            xw.WriteEndElement();
            xw.WriteEndElement();
            xw.WriteEndDocument();
        }
    }

    private static void WriteWorkbookRelationships(ZipArchive archive)
    {
        using (Stream stream = CreateEntry(archive, "xl/_rels/workbook.xml.rels"))
        using (XmlWriter xw = CreateXmlWriter(stream))
        {
            xw.WriteStartDocument(true);
            xw.WriteStartElement("Relationships", PackageRelationshipsNs);
            for (int i = 0; i < DeviceCount; i++)
            {
                WriteRelationship(xw, "rId" + (i + 1),
                    "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet",
                    "worksheets/sheet" + (i + 1) + ".xml");
            }
            WriteRelationship(xw, "rId" + (DeviceCount + 1),
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles",
                "styles.xml");
            xw.WriteEndElement();
            xw.WriteEndDocument();
        }
    }

    private static void WriteStyles(ZipArchive archive)
    {
        const string xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"0.0000\"/></numFmts>" +
            "<fonts count=\"2\">" +
            "<font><sz val=\"11\"/><name val=\"Microsoft YaHei\"/><family val=\"2\"/></font>" +
            "<font><b/><color rgb=\"FFFFFFFF\"/><sz val=\"11\"/><name val=\"Microsoft YaHei\"/><family val=\"2\"/></font>" +
            "</fonts>" +
            "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1F4E78\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
            "<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border>" +
            "<border><left style=\"thin\"><color rgb=\"FFD9E2F3\"/></left><right style=\"thin\"><color rgb=\"FFD9E2F3\"/></right>" +
            "<top style=\"thin\"><color rgb=\"FFD9E2F3\"/></top><bottom style=\"thin\"><color rgb=\"FFD9E2F3\"/></bottom><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"4\">" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
            "<xf numFmtId=\"2\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
            "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
            "</cellXfs>" +
            "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
            "<dxfs count=\"0\"/><tableStyles count=\"0\" defaultTableStyle=\"TableStyleMedium2\" defaultPivotStyle=\"PivotStyleLight16\"/>" +
            "</styleSheet>";

        using (Stream stream = CreateEntry(archive, "xl/styles.xml"))
        using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            writer.Write(xml);
    }

    private static void WriteWorksheet(ZipArchive archive, int deviceId, List<TelemetryRow> rows)
    {
        string[] headers = GetHeaders(deviceId);
        int lastColumn = headers.Length;
        string lastCell = ColumnName(lastColumn) + Math.Max(1, rows.Count + 1).ToString(Invariant);

        using (Stream stream = CreateEntry(archive, "xl/worksheets/sheet" + (deviceId + 1) + ".xml"))
        using (XmlWriter xw = CreateXmlWriter(stream))
        {
            xw.WriteStartDocument(true);
            xw.WriteStartElement("worksheet", SpreadsheetNs);
            xw.WriteStartElement("dimension", SpreadsheetNs);
            xw.WriteAttributeString("ref", "A1:" + lastCell);
            xw.WriteEndElement();

            xw.WriteStartElement("sheetViews", SpreadsheetNs);
            xw.WriteStartElement("sheetView", SpreadsheetNs);
            xw.WriteAttributeString("workbookViewId", "0");
            xw.WriteStartElement("pane", SpreadsheetNs);
            xw.WriteAttributeString("ySplit", "1");
            xw.WriteAttributeString("topLeftCell", "A2");
            xw.WriteAttributeString("activePane", "bottomLeft");
            xw.WriteAttributeString("state", "frozen");
            xw.WriteEndElement();
            xw.WriteEndElement();
            xw.WriteEndElement();

            WriteColumns(xw, headers.Length);

            xw.WriteStartElement("sheetData", SpreadsheetNs);
            xw.WriteStartElement("row", SpreadsheetNs);
            xw.WriteAttributeString("r", "1");
            xw.WriteAttributeString("ht", "24");
            xw.WriteAttributeString("customHeight", "1");
            for (int c = 0; c < headers.Length; c++)
                WriteInlineStringCell(xw, ColumnName(c + 1) + "1", headers[c], 1);
            xw.WriteEndElement();

            for (int i = 0; i < rows.Count; i++)
            {
                int rowNumber = i + 2;
                TelemetryRow row = rows[i];
                xw.WriteStartElement("row", SpreadsheetNs);
                xw.WriteAttributeString("r", rowNumber.ToString(Invariant));

                int col = 1;
                WriteInlineStringCell(xw, ColumnName(col++) + rowNumber, row.Timestamp, 0);
                WriteNumberCell(xw, ColumnName(col++) + rowNumber, row.Frame, 0);
                WriteNumberCell(xw, ColumnName(col++) + rowNumber, row.DeviceId + 1, 0);
                WriteNumberCell(xw, ColumnName(col++) + rowNumber, row.ProtocolVersion, 0);
                WriteInlineStringCell(xw, ColumnName(col++) + rowNumber,
                    row.HardwareId == 0 ? string.Empty : row.HardwareId.ToString("X8", Invariant), 0);
                WriteNumberCell(xw, ColumnName(col++) + rowNumber, row.SourceSequence, 0);
                WriteNumberCell(xw, ColumnName(col++) + rowNumber, row.SenderTickMs, 0);
                WriteNumberCell(xw, ColumnName(col++) + rowNumber, row.SourceLostFrames, 0);
                WriteNumberCell(xw, ColumnName(col++) + rowNumber, row.SourceDuplicateFrames, 0);
                WriteNumberCell(xw, ColumnName(col++) + rowNumber, row.SourceOutOfOrderFrames, 0);
                WriteNumberCell(xw, ColumnName(col++) + rowNumber, row.DuplicateLogicalIdFrames, 0);
                WriteNumberCell(xw, ColumnName(col++) + rowNumber, row.ReceiveFrameRateHz, 2);
                WriteNumberCell(xw, ColumnName(col++) + rowNumber, row.SourceReportedFrameRateHz, 2);
                WriteNumberCell(xw, ColumnName(col++) + rowNumber, row.SourceDeliveryPercent, 2);
                WriteNumberCell(xw, ColumnName(col++) + rowNumber, row.Q0, 3);
                WriteNumberCell(xw, ColumnName(col++) + rowNumber, row.Q1, 3);
                WriteNumberCell(xw, ColumnName(col++) + rowNumber, row.Q2, 3);
                WriteNumberCell(xw, ColumnName(col++) + rowNumber, row.Q3, 3);
                WriteNumberCell(xw, ColumnName(col++) + rowNumber, row.Yaw, 2);
                WriteNumberCell(xw, ColumnName(col++) + rowNumber, row.Pitch, 2);
                WriteNumberCell(xw, ColumnName(col++) + rowNumber, row.Roll, 2);

                if (deviceId == (int)BoneIndex.LeftForeArm || deviceId == (int)BoneIndex.RightForeArm)
                    WriteNumberCell(xw, ColumnName(col++) + rowNumber, row.ElbowFlexionDeg, 2);

                if (deviceId == (int)BoneIndex.LeftLeg || deviceId == (int)BoneIndex.RightLeg)
                {
                    WriteNumberCell(xw, ColumnName(col++) + rowNumber, row.KneeFlexionDeg, 2);
                    WriteNumberCell(xw, ColumnName(col++) + rowNumber, row.KneeIncludedDeg, 2);
                }

                xw.WriteEndElement();
            }
            xw.WriteEndElement();

            xw.WriteStartElement("autoFilter", SpreadsheetNs);
            xw.WriteAttributeString("ref", "A1:" + ColumnName(lastColumn) + Math.Max(1, rows.Count + 1).ToString(Invariant));
            xw.WriteEndElement();
            xw.WriteEndElement();
            xw.WriteEndDocument();
        }
    }

    private static string[] GetHeaders(int deviceId)
    {
        var headers = new List<string>
        {
            "记录时间",
            "帧序号",
            "传感器编号",
            "协议版本",
            "硬件唯一ID",
            "源帧序号",
            "发送端时钟(ms)",
            "源端累计丢帧",
            "源端累计重复帧",
            "源端累计乱序帧",
            "重复逻辑ID冲突",
            "Unity实际接收Hz",
            "控制板实际发送Hz",
            "链路到达率(%)",
            "四元数X",
            "四元数Y",
            "四元数Z",
            "四元数W",
            "偏航角(°)",
            "俯仰角(°)",
            "横滚角(°)"
        };

        if (deviceId == (int)BoneIndex.LeftForeArm)
            headers.Add("左肘屈曲角(°)");
        else if (deviceId == (int)BoneIndex.RightForeArm)
            headers.Add("右肘屈曲角(°)");

        if (deviceId == (int)BoneIndex.LeftLeg)
        {
            headers.Add("左膝屈曲角(°)");
            headers.Add("左大腿-小腿夹角(°)");
        }
        else if (deviceId == (int)BoneIndex.RightLeg)
        {
            headers.Add("右膝屈曲角(°)");
            headers.Add("右大腿-小腿夹角(°)");
        }

        return headers.ToArray();
    }

    private static void WriteColumns(XmlWriter xw, int columnCount)
    {
        xw.WriteStartElement("cols", SpreadsheetNs);
        for (int i = 1; i <= columnCount; i++)
        {
            double width;
            if (i == 1) width = 31;
            else if (i == 2) width = 12;
            else if (i == 3) width = 13;
            else if (i >= 4 && i <= 7) width = 13;
            else width = 22;

            xw.WriteStartElement("col", SpreadsheetNs);
            xw.WriteAttributeString("min", i.ToString(Invariant));
            xw.WriteAttributeString("max", i.ToString(Invariant));
            xw.WriteAttributeString("width", width.ToString("0.##", Invariant));
            xw.WriteAttributeString("customWidth", "1");
            xw.WriteEndElement();
        }
        xw.WriteEndElement();
    }

    private static void WriteInlineStringCell(XmlWriter xw, string reference, string value, int style)
    {
        xw.WriteStartElement("c", SpreadsheetNs);
        xw.WriteAttributeString("r", reference);
        xw.WriteAttributeString("t", "inlineStr");
        if (style > 0) xw.WriteAttributeString("s", style.ToString(Invariant));
        xw.WriteStartElement("is", SpreadsheetNs);
        xw.WriteStartElement("t", SpreadsheetNs);
        xw.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
        xw.WriteString(value ?? string.Empty);
        xw.WriteEndElement();
        xw.WriteEndElement();
        xw.WriteEndElement();
    }

    private static void WriteNumberCell(XmlWriter xw, string reference, double value, int style)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return;

        xw.WriteStartElement("c", SpreadsheetNs);
        xw.WriteAttributeString("r", reference);
        if (style > 0) xw.WriteAttributeString("s", style.ToString(Invariant));
        xw.WriteElementString("v", SpreadsheetNs, value.ToString("R", Invariant));
        xw.WriteEndElement();
    }

    private static string ColumnName(int columnNumber)
    {
        var sb = new StringBuilder(4);
        int n = columnNumber;
        while (n > 0)
        {
            n--;
            sb.Insert(0, (char)('A' + (n % 26)));
            n /= 26;
        }
        return sb.ToString();
    }
}
