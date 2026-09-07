using OpenSim.Framework;

namespace OpenSim.Data
{
    public interface IStarterLookData
    {
        /// <summary>
        /// Inserts a new look (LookID is assigned by the DB, always excluded
        /// from the INSERT so auto-increment/serial still works).
        /// </summary>
        bool Store(StarterLookData data);

        /// <summary>
        /// Already provided by MySQLGenericTableHandler&lt;StarterLookData&gt;
        /// for any implementation that derives from it - declared here so the
        /// service layer can call it through the interface.
        /// </summary>
        StarterLookData[] Get(string field, string key);
        StarterLookData[] Get(string where);

        /// <summary>
        /// Updates an existing look's editable fields (Name/ModelAccountID/
        /// SortOrder/Enabled) by LookID. Not the same as Store() - Store()
        /// always INSERTs a new row.
        /// </summary>
        bool Update(StarterLookData data);

        bool Delete(string field, string key);
    }
}
