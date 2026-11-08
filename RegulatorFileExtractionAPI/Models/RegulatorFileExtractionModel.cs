using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace RegulatorFileExtractionAPI.Models
{
    public class FileExtractionModelFromURL
    {
        public string FileId { get; set; } = null;
        public string FilePublishDate { get; set; } = null;
        public string FileName { get; set; } = null;
        public string RegulatorName { get; set; } = null;
        public string RegulatorKey { get; set; } = null;
        public string CircularName { get; set; } = null;
        public string CircularKey { get; set; } = null;
        public byte[] FileAsBytes { get; set; } = null;
    }
    public class APIRequest
    {
        public string RegulatorName { get; set; } = null;
        public string RegulatorKey { get; set; } = null;
        public string CircularName { get; set; } = null;
        public string CircularKey { get; set; } = null;
        public string Year { get; set; } = null;
    }
    public class APIResponse
    {
        public string Status { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }
        public  object Data { get; set; }
    }
}