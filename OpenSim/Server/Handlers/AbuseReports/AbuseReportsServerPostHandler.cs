using Nini.Config;
using log4net;
using System;
using System.Reflection;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;
using System.Collections.Generic;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Framework;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Framework.Servers.HttpServer;
using OpenMetaverse;

namespace OpenSim.Server.Handlers.AbuseReports
{
    public class AbuseReportsServerPostHandler : BaseStreamHandler
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private IAbuseReportsService m_service;
        private string m_AdminSecret;

        public AbuseReportsServerPostHandler(IAbuseReportsService service, IServiceAuth auth) :
                this(service, auth, String.Empty)
        {
        }

        public AbuseReportsServerPostHandler(IAbuseReportsService service, IServiceAuth auth, string adminSecret) :
                base("POST", "/abuse", auth)
        {
            m_service = service;
            m_AdminSecret = adminSecret ?? String.Empty;
        }

        protected override byte[] ProcessRequest(string path, Stream requestData,
                IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            string body;
            using(StreamReader sr = new StreamReader(requestData))
                body = sr.ReadToEnd();
            body = body.Trim();

            //m_log.DebugFormat("[XXX]: query String: {0}", body);
            string method = string.Empty;

            try
            {
                Dictionary<string, object> request =
                        ServerUtils.ParseQueryString(body);

                if (!request.ContainsKey("METHOD"))
                    return FailureResult();

                method = request["METHOD"].ToString();

                switch (method)
                {
                    case "report":
                        return report(request);
                    case "getreports":
                        return getreports(request);
                    case "getreport":
                        return getreport(request);
                }
                m_log.DebugFormat("[ABUSE REPORT HANDLER]: unknown method request: {0}", method);
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[ABUSE REPORT HANDLER]: Exception in method {0}: {1}", method, e);
            }

            return FailureResult();
        }

        byte[] report(Dictionary<string, object> request)
        {
            if(!request.ContainsKey("reporter") || !request.ContainsKey("abuser"))
                return FailureResult();

            AbuseReportData report = new AbuseReportData();

            if( !UUID.TryParse(request["reporter"].ToString(), out report.SenderID))
                return FailureResult();

            if(request.ContainsKey("reporter-name"))
				report.SenderName = request["reporter-name"].ToString();

            if(!UUID.TryParse(request["abuser"].ToString(), out report.AbuserID))
                return FailureResult();

            if(request.ContainsKey("abuser-name"))
				report.AbuserName = request["abuser-name"].ToString();

            if(!UUID.TryParse(request["region-id"].ToString(), out report.AbuseRegionID))
                return FailureResult();

            if(request.ContainsKey("region-name"))
				report.AbuseRegionName = request["region-name"].ToString();

            report.Time = Util.UnixTimeSinceEpoch();
                
            if(request.ContainsKey("summary"))
				report.Summary = request["summary"].ToString();

            if(request.ContainsKey("details"))
				report.Details = request["details"].ToString();

            if(request.ContainsKey("version"))
				report.Version = request["version"].ToString();

            if(request.ContainsKey("object-id"))
            {
                if(!UUID.TryParse(request["object-id"].ToString(), out report.ObjectID))
                    return FailureResult();
            }
            else
                report.ObjectID = UUID.Zero;

            if(request.ContainsKey("position"))
				report.Position = request["position"].ToString();

            if(request.ContainsKey("category"))
				report.Category = request["category"].ToString();

            if(request.ContainsKey("check-flags"))
            {
                if(!Int32.TryParse(request["check-flags"].ToString(), out report.CheckFlags))
                    return FailureResult();
            }
            else
                report.CheckFlags = 0;

            if(request.ContainsKey("image-data"))
                report.ImageData = Convert.FromBase64String(request["image-data"].ToString());
            else report.ImageData = new byte[0];

            m_log.InfoFormat("[ABUSE REPORTS] {0} has reported {1}", report.SenderName, report.AbuserName);

            return m_service.ReportAbuse(report) ? SuccessResult() : FailureResult();
        }

        private bool CheckAdminSecret(Dictionary<string, object> request)
        {
            if (String.IsNullOrEmpty(m_AdminSecret))
                return false;

            if (!request.ContainsKey("adminsecret"))
                return false;

            return request["adminsecret"].ToString() == m_AdminSecret;
        }

        private Dictionary<string, object> AbuseReportToKeyValuePairs(AbuseReportData r)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["reportid"] = r.ReportID.ToString();
            d["reporter"] = r.SenderID.ToString();
            d["reporter-name"] = r.SenderName;
            d["abuser"] = r.AbuserID.ToString();
            d["abuser-name"] = r.AbuserName;
            d["time"] = r.Time.ToString();
            d["region-id"] = r.AbuseRegionID.ToString();
            d["region-name"] = r.AbuseRegionName;
            d["category"] = r.Category;
            d["check-flags"] = r.CheckFlags.ToString();
            d["details"] = r.Details;
            d["object-id"] = r.ObjectID.ToString();
            d["position"] = r.Position;
            d["report-type"] = r.ReportType.ToString();
            d["summary"] = r.Summary;
            d["version"] = r.Version;
            return d;
        }

        byte[] getreports(Dictionary<string, object> request)
        {
            if (!CheckAdminSecret(request))
                return FailureResult();

            int start = 0;
            if (request.ContainsKey("start"))
                Int32.TryParse(request["start"].ToString(), out start);

            int count = -1;
            if (request.ContainsKey("count"))
                Int32.TryParse(request["count"].ToString(), out count);

            List<AbuseReportData> reports = m_service.GetAbuseReports(start, count);

            Dictionary<string, object> result = new Dictionary<string, object>();
            if (reports == null || reports.Count == 0)
                result["result"] = "null";
            else
            {
                int i = 0;
                foreach (AbuseReportData r in reports)
                {
                    result["report" + i] = AbuseReportToKeyValuePairs(r);
                    i++;
                }
            }

            string xmlString = ServerUtils.BuildXmlResponse(result);
            return Util.UTF8NoBomEncoding.GetBytes(xmlString);
        }

        byte[] getreport(Dictionary<string, object> request)
        {
            if (!CheckAdminSecret(request))
                return FailureResult();

            if (!request.ContainsKey("reportid"))
                return FailureResult();

            int reportID;
            if (!Int32.TryParse(request["reportid"].ToString(), out reportID))
                return FailureResult();

            AbuseReportData r = m_service.GetAbuseReport(reportID);

            Dictionary<string, object> result = new Dictionary<string, object>();
            if (r == null)
                result["result"] = "null";
            else
                result["report0"] = AbuseReportToKeyValuePairs(r);

            string xmlString = ServerUtils.BuildXmlResponse(result);
            return Util.UTF8NoBomEncoding.GetBytes(xmlString);
        }

        private byte[] SuccessResult()
        {
            XmlDocument doc = new XmlDocument();

            XmlNode xmlnode = doc.CreateNode(XmlNodeType.XmlDeclaration,
                    "", "");

            doc.AppendChild(xmlnode);

            XmlElement rootElement = doc.CreateElement("", "ServerResponse",
                    "");

            doc.AppendChild(rootElement);

            XmlElement result = doc.CreateElement("", "result", "");
            result.AppendChild(doc.CreateTextNode("Success"));

            rootElement.AppendChild(result);

            return Util.DocToBytes(doc);
        }

        private byte[] FailureResult()
        {
            XmlDocument doc = new XmlDocument();

            XmlNode xmlnode = doc.CreateNode(XmlNodeType.XmlDeclaration,
                    "", "");

            doc.AppendChild(xmlnode);

            XmlElement rootElement = doc.CreateElement("", "ServerResponse",
                    "");

            doc.AppendChild(rootElement);

            XmlElement result = doc.CreateElement("", "result", "");
            result.AppendChild(doc.CreateTextNode("Failure"));

            rootElement.AppendChild(result);

            return Util.DocToBytes(doc);
        }
    }
}
