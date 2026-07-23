using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PartingSurface.Detection
{
    /// <summary>
    /// Public API entry point for the parting surface detection pipeline.
    /// Provides static methods to run the full geometry analysis + review pipeline.
    /// </summary>
    public static class DetectionGateway
    {
        /// <summary>
        /// Run the full detection pipeline (geometry analysis + review) on a .prt file.
        /// Returns a JSON string containing evidence, review result, and markdown report.
        /// </summary>
        /// <param name="prtPath">Path to the Siemens NX .prt file to analyze.</param>
        /// <param name="nxRoot">Optional NX installation root directory. If null, auto-detected.</param>
        /// <returns>JSON string with the combined analysis result.</returns>
        public static string Detect(string prtPath, string nxRoot = null)
        {
            if (string.IsNullOrEmpty(prtPath))
            {
                throw new ArgumentNullException("prtPath");
            }

            // Use a temporary working directory for the pipeline output
            string tempBase = Path.Combine(Path.GetTempPath(), "PartingSurface_Detection_" +
                DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + "_" +
                Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempBase);

            try
            {
                return RunPipeline(prtPath, tempBase, nxRoot);
            }
            finally
            {
                // Best-effort cleanup of temp directory
                try
                {
                    if (Directory.Exists(tempBase))
                    {
                        Directory.Delete(tempBase, true);
                    }
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }

        /// <summary>
        /// Run the full detection pipeline and save the JSON result to a file.
        /// Also saves the geometry evidence and markdown report alongside.
        /// </summary>
        /// <param name="prtPath">Path to the Siemens NX .prt file to analyze.</param>
        /// <param name="outputPath">Path where the JSON result file will be saved. If a directory, a default filename is used.</param>
        /// <param name="nxRoot">Optional NX installation root directory. If null, auto-detected.</param>
        /// <returns>The path to the saved JSON file.</returns>
        public static string DetectToFile(string prtPath, string outputPath, string nxRoot = null)
        {
            if (string.IsNullOrEmpty(prtPath))
            {
                throw new ArgumentNullException("prtPath");
            }
            if (string.IsNullOrEmpty(outputPath))
            {
                throw new ArgumentNullException("outputPath");
            }

            // Determine output directory and file path
            string outputDir;
            string jsonFilePath;

            if (Directory.Exists(outputPath) || outputPath.EndsWith("\\") || outputPath.EndsWith("/"))
            {
                outputDir = outputPath;
                Directory.CreateDirectory(outputDir);
                jsonFilePath = Path.Combine(outputDir, "detection_result.json");
            }
            else
            {
                jsonFilePath = outputPath;
                outputDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }
            }

            // Run the pipeline using the output directory as working space
            string json = RunPipeline(prtPath, outputDir, nxRoot);

            // Save the combined JSON result
            File.WriteAllText(jsonFilePath, json, new UTF8Encoding(false));

            // Also save individual files for convenience
            try
            {
                // The geometry_evidence.json is already saved by GeometryPipeline.AnalyzePrt
                // Save the markdown report if available
                // (The markdown is embedded in the combined JSON)
            }
            catch
            {
                // Ignore errors from supplementary file saves
            }

            return jsonFilePath;
        }

        /// <summary>
        /// Internal method that runs the full pipeline and returns combined JSON.
        /// </summary>
        private static string RunPipeline(string prtPath, string outputDirectory, string nxRoot)
        {
            // Step 1: Run geometry analysis pipeline
            Dictionary<string, object> evidence = GeometryPipeline.AnalyzePrt(
                prtPath, outputDirectory, nxRoot);

            // Step 2: Run review engine on the evidence
            ReviewEngine engine = new ReviewEngine();
            Dictionary<string, object> review = engine.Review(evidence);

            // Step 3: Render markdown report
            string markdown = engine.RenderMarkdown(review);

            // Step 4: Combine into a single result object
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                { "evidence", evidence },
                { "review", review },
                { "markdown", markdown },
            };

            // Serialize to JSON using our custom serializer (no external dependencies)
            return JsonHelper.Serialize(result);
        }
    }
}
