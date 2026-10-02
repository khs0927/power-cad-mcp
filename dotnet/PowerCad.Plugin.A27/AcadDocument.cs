using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using PowerCad.Core;
using PowerCad.Core.Model;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace PowerCad.Plugin;

/// <summary>
/// <see cref="ICadDocument"/> over the active AutoCAD document. Each call: marshal to the main thread →
/// lock the document → one database transaction → commit or abort. An exception anywhere (including a
/// failed postcondition in the harness) aborts the transaction, so AutoCAD's drawing is untouched.
/// </summary>
internal sealed class AcadDocument(MainThreadInvoker invoker, TimeSpan timeout) : ICadDocument
{
    private const string UndoGroupName = "POWERCAD_EDIT";

    private sealed class OpenDatabaseIdentity
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
    }

    private static readonly ConditionalWeakTable<Database, OpenDatabaseIdentity> Identities = new();
    private static string Identity(Database db) => Identities.GetValue(db, _ => new OpenDatabaseIdentity()).Id;

    public JsonObject Describe(string? expectedDocumentId = null) => invoker.Invoke(
        () =>
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            var info = new JsonObject
            {
                ["backend"] = "autocad",
                ["application"] = $"AutoCAD {AcApp.Version}",
                ["plugin_version"] = typeof(AcadDocument).Assembly.GetName().Version?.ToString(3),
                ["open_documents"] = AcApp.DocumentManager.Count,
                ["documents"] = new JsonArray(AcApp.DocumentManager.Cast<Autodesk.AutoCAD.ApplicationServices.Document>()
                    .Select(d => (JsonNode)System.IO.Path.GetFileName(d.Name)).ToArray()),
            };
            if (doc is null)
            {
                if (expectedDocumentId is not null)
                    throw new CadException(ErrorCodes.DocumentChanged, "The bound drawing is no longer active.");
                info["document"] = null;
                return info;
            }

            var db = doc.Database;
            DocumentBinding.Verify(Identity(db), expectedDocumentId);
            info["document_id"] = Identity(db);
            info["session_id"] = $"autocad-2027-{Environment.ProcessId}";
            info["database_fingerprint"] = db.FingerprintGuid.ToString();
            info["identity_scope"] = "open_database";
            info["document"] = doc.Name;
            info["units"] = db.Insunits.ToString();
            using var tr = db.TransactionManager.StartOpenCloseTransaction();
            var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            info["entity_count"] = ms.Cast<ObjectId>().Count();
            info["current_layer"] = ((LayerTableRecord)tr.GetObject(db.Clayer, OpenMode.ForRead)).Name;
            info["model_space_active"] = db.TileMode;
            tr.Commit();
            return info;
        },
        timeout);

    public T Execute<T>(Func<ICadTransaction, T> work, bool commit, string? expectedDocumentId = null) => invoker.Invoke(
        () =>
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument
                ?? throw new CadException(ErrorCodes.NoDocument, "No drawing is open in AutoCAD.", "Open or create a drawing first.");
            DocumentLockGuard docLock;
            try
            {
                // A named write lock makes AutoCAD record everything done under it as one undo group
                // ("POWERCAD_EDIT"), so a single UNDO reverts exactly one request.
                docLock = new DocumentLockGuard(doc.LockDocument(DocumentLockMode.Write, UndoGroupName, UndoGroupName, false));
            }
            catch (Autodesk.AutoCAD.Runtime.Exception e) when (e.ErrorStatus == ErrorStatus.LockViolation)
            {
                throw new CadException(ErrorCodes.Busy, "The drawing is locked by a running command.", "Finish or cancel the command in AutoCAD (Esc) and retry.");
            }

            using (docLock)
            {
                DocumentBinding.Verify(Identity(doc.Database), expectedDocumentId);
                var db = doc.Database;
                using var tr = db.TransactionManager.StartTransaction();
                try
                {
                    var result = work(new AcadTransaction(db, tr));
                    if (commit)
                    {
                        tr.Commit();
                        doc.Editor.Regen();
                    }
                    else
                    {
                        tr.Abort();
                    }

                    return result;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception e)
                {
                    tr.Abort();
                    throw Translate(e);
                }
                catch
                {
                    tr.Abort();
                    throw;
                }
            }
        },
        timeout);

    public JsonObject View(PowerCad.Core.Model.Vec3 min, PowerCad.Core.Model.Vec3 max, int? snapshotWidth, int? snapshotHeight, string? expectedDocumentId = null) => invoker.Invoke(
        () =>
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument
                ?? throw new CadException(ErrorCodes.NoDocument, "No drawing is open in AutoCAD.", "Open or create a drawing first.");
            if (!doc.Database.TileMode)
            {
                throw new CadException(ErrorCodes.Unsupported, "A layout tab is active; zoom/snapshot work on the Model tab.", "Ask the user to switch to the Model tab.");
            }

            using (doc.LockDocument())
            {
                DocumentBinding.Verify(Identity(doc.Database), expectedDocumentId);
                var ed = doc.Editor;
                using var view = ed.GetCurrentView();
                var wcsToDcs = (Matrix3d.PlaneToWorld(view.ViewDirection)
                    * Matrix3d.Displacement(view.Target - Point3d.Origin)
                    * Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target)).Inverse();
                var ext = new Extents3d(new Point3d(min.X, min.Y, min.Z), new Point3d(max.X, max.Y, max.Z));
                ext.TransformBy(wcsToDcs);
                var w = Math.Max(1e-6, ext.MaxPoint.X - ext.MinPoint.X);
                var h = Math.Max(1e-6, ext.MaxPoint.Y - ext.MinPoint.Y);
                if (snapshotWidth is { } sw && snapshotHeight is { } sh)
                {
                    // Match the view to the image aspect so the requested window fills the picture.
                    var aspect = (double)sw / sh;
                    if (w / h > aspect)
                    {
                        h = w / aspect;
                    }
                    else
                    {
                        w = h * aspect;
                    }
                }

                view.Width = w;
                view.Height = h;
                view.CenterPoint = new Point2d((ext.MinPoint.X + ext.MaxPoint.X) / 2, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2);
                ed.SetCurrentView(view);
                ed.UpdateScreen();

                var result = new JsonObject { ["zoomed"] = true };
                if (snapshotWidth is { } iw && snapshotHeight is { } ih)
                {
                    // AutoCAD fits the view to the viewport's aspect, and CapturePreviewImage renders the whole
                    // viewport (keeping its aspect), so capture large enough and crop out the requested window.
                    using var actual = ed.GetCurrentView();
                    var fx = Math.Min(1, w / actual.Width);
                    var fy = Math.Min(1, h / actual.Height);
                    var capW = iw / fx;
                    var capH = ih / fy;
                    var shrink = Math.Min(1, 8000 / Math.Max(capW, capH));
                    AcApp.UpdateScreen();
                    using var full = doc.CapturePreviewImage((uint)Math.Ceiling(capW * shrink), (uint)Math.Ceiling(capH * shrink));
                    var dx = (actual.CenterPoint.X - view.CenterPoint.X) / actual.Width;
                    var dy = (actual.CenterPoint.Y - view.CenterPoint.Y) / actual.Height;
                    var src = new System.Drawing.RectangleF(
                        (float)((0.5 - (fx / 2) - dx) * full.Width),
                        (float)((0.5 - (fy / 2) + dy) * full.Height),
                        (float)(fx * full.Width),
                        (float)(fy * full.Height));
                    using var bmp = new System.Drawing.Bitmap(iw, ih);
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(full, new System.Drawing.Rectangle(0, 0, iw, ih), src, System.Drawing.GraphicsUnit.Pixel);
                    }

                    using var ms = new MemoryStream();
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    result["mime_type"] = "image/png";
                    result["width"] = bmp.Width;
                    result["height"] = bmp.Height;
                    result["image_base64"] = Convert.ToBase64String(ms.ToArray());
                }

                return result;
            }
        },
        timeout);

    public JsonObject Save(SaveRequest request, string? expectedDocumentId = null) => invoker.Invoke(
        () =>
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument
                ?? throw new CadException(ErrorCodes.NoDocument, "No drawing is open in AutoCAD.", "Open or create a drawing first.");
            using (doc.LockDocument())
            {
                DocumentBinding.Verify(Identity(doc.Database), expectedDocumentId);
                var db = doc.Database;
                string path;
                if (request.Copy)
                {
                    // Wblock clones the whole database, so the open drawing (its name, dirty flag) is untouched.
                    path = request.Path!;
                    using var clone = db.Wblock();
                    if (request.Format == "dxf")
                    {
                        clone.DxfOut(path, 16, DwgVersion.Current);
                    }
                    else
                    {
                        clone.SaveAs(path, DwgVersion.Current);
                    }
                }
                else
                {
                    path = request.Path ?? (doc.IsNamedDrawing
                        ? doc.Name
                        : throw new CadException(ErrorCodes.InvalidParams, $"'{doc.Name}' has never been saved.", "Give 'path' for the first save."));
                    db.SaveAs(path, true, DwgVersion.Current, db.SecurityParameters);
                }

                return new JsonObject
                {
                    ["path"] = path,
                    ["format"] = request.Format,
                    ["bytes"] = new FileInfo(path).Length,
                };
            }
        },
        timeout);

    internal static CadException Translate(Autodesk.AutoCAD.Runtime.Exception e) => e.ErrorStatus switch
    {
        ErrorStatus.OnLockedLayer => new CadException(ErrorCodes.LockedLayer, "The entity is on a locked layer.", "Ask the user before unlocking it."),
        ErrorStatus.WasErased or ErrorStatus.NullObjectId =>
            new CadException(ErrorCodes.NotFound, "The entity no longer exists.", "Re-query the drawing."),
        ErrorStatus.NotApplicable or ErrorStatus.NotImplementedYet =>
            new CadException(ErrorCodes.Unsupported, $"AutoCAD rejected the operation ({e.ErrorStatus})."),
        _ => new CadException(ErrorCodes.Internal, $"AutoCAD error {e.ErrorStatus}: {e.Message}"),
    };

    private sealed class DocumentLockGuard(IDisposable inner) : IDisposable
    {
        public void Dispose() => inner.Dispose();
    }
}
