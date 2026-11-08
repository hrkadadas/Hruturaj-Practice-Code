using RegulatorFileExtractionAPI.Models;
using RegulatorFileExtractionAPI.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;

namespace RegulatorFileExtractionAPI.Controllers
{
    [RoutePrefix("api/controller")]
    public class RegulatorFileExtractionController : ApiController
    {
        public RegulatorFileExtractionController()
        {
            
        }

        [HttpPost]
        [Route("extract-files")]
        public async Task<IHttpActionResult> ExtractFiles(APIRequest request)
        {
            var response = new APIResponse();

            try
            {
                if (request == null)
                {
                    response.Status = "Failed";
                    response.Message = "Invalid request.";
                    response.Error = "Request cannot be null.";
                    response.Data = null;

                    return BadRequest(response.Message);
                }

                if (string.IsNullOrWhiteSpace(request.RegulatorName))
                {
                    response.Status = "Failed";
                    response.Message = "RegulatorName is required.";
                    response.Error = null;
                    response.Data = null;

                    return Ok(response);
                }              

                if (string.IsNullOrWhiteSpace(request.RegulatorKey) && string.IsNullOrWhiteSpace(request.Year))
                {
                    response.Status = "Failed";
                    response.Message = "RegulatorKey is required.";
                    response.Error = null;
                    response.Data = null;

                    return Ok(response);
                }
                List<FileExtractionModelFromURL> files = null;
                if (request.RegulatorName.ToLower() == "ifsca")
                {
                    //var service = new IfscaScraperService();
                    //files = await service.GetLatestRegulationsAsync(request);
                    var service = new IfscaScraperService_v1();
                    files = await service.GetLatestRegulationsFromAjaxAsync(request);
                    
                }
                if (request.RegulatorName.ToLower() == "mauritius-regulations")
                {
                    var service = new MauritiusRegulationsService();

                    files = await service.GetLatestRegulationsAsync(request);

                }
                if (request.CircularName.ToLower() == "mauritius-circulars" && !string.IsNullOrWhiteSpace(request.Year))
                {
                    var service = new MauritiusCircularsService();

                    files = await service.GetLatestCircularsAsync(request);
                }
                //if (request.RegulatorName.ToLower() == "ifsca")
                //{ }
                else
                {
                    response.Status = "Failed";
                    response.Message = "Unsupported RegulatorName.";
                    response.Error = null;
                    response.Data = null;
                    return Ok(response);
                }
                response.Status = "Success";

                response.Message = files.Count > 0 ? "Files extracted successfully." : "No matching files found.";

                response.Error = null;
                response.Data = files;

                return Ok(response);
            }
            catch (Exception ex)
            {
                response.Status = "Failed";
                response.Message = "File extraction failed.";
                response.Error = ex.Message;
                response.Data = null;

                return Ok(response);
            }
        }
    }
}
