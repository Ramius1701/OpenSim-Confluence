using OpenMetaverse;

namespace OpenSim.Framework
{
    public class AbuseReportData
    {
		public int ReportID; // Handled by SQL
        public UUID SenderID;
        public string SenderName;
        public int Time; // Always filled in via the ROBUST
        
        public UUID AbuseRegionID = UUID.Zero; // Filled in via the region
        public string AbuseRegionName; // Filled in via the region
        public UUID AbuserID;
        public string AbuserName;
        public string Category;
        public int CheckFlags = 0;
        public string Details;
        public UUID ObjectID = UUID.Zero;
        public string Position;
        public int ReportType;
        public string Summary;
        public string Version;
        public byte[] ImageData;

        // Admin-tool fields (WebUI abuse-reports queue) - not part of the
        // viewer's own SendUserReportWithScreenshot submission, only ever
        // set/read from the admin side. Active defaults true (a freshly
        // submitted report is open until an admin says otherwise).
        public bool Active = true;
        public string AssignedTo = string.Empty;
        public string Notes = string.Empty;
    }
}
