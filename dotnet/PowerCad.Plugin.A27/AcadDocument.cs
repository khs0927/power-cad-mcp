using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
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
    public JsonObject Describe() => invoker.Invoke(
        () =>
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            var info = new JsonObject
            {
                ["backend"] = "autocad",
                ["application"] = $"AutoCAD {AcApp.Version}",
                ["plugin_version"] = typeof(AcadDocument).Assembly.GetName().Version?.ToString(3),
                ["open_documents"] = AcApp.DocumentManager.Count,
            };
            if (doc is null)
            {
                info["document"] = null;
                return info;
            }

            var db = doc.Database;
            info["document"] = doc.Name;
            info["units"] = db.Insunits.ToString();
            using var tr = db.TransactionManager.StartOpenCloseTransaction();
            var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            info["entity_count"] = ms.Cast<ObjectId>().Count();
            tr.Commit();
            return info;
        },
        timeout);

    public T Execute<T>(Func<ICadTransaction, T> work, bool commit) => invoker.Invoke(
        () =>
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument
                ?? throw new CadException(ErrorCodes.NoDocument, "No drawing is open in AutoCAD.", "Open or create a drawing first.");
            DocumentLockGuard docLock;
            try
            {
                docLock = new DocumentLockGuard(doc.LockDocument());
            }
            catch (Autodesk.AutoCAD.Runtime.Exception e) when (e.ErrorStatus == ErrorStatus.LockViolation)
            {
                throw new CadException(ErrorCodes.Busy, "The drawing is locked by a running command.", "Finish or cancel the command in AutoCAD (Esc) and retry.");
            }

            using (docLock)
            {
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
