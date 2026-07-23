using System;
using System.Collections.Generic;

namespace PartingSurfaceReview
{
    public enum IssueSeverity { ERROR, WARN, INFO }

    public enum RepairActionType
    {
        FaceDeleteAndHeal,
        FaceJoin,
        EdgeBlend,
        ManualReview
    }

    public class ReviewIssue
    {
        public string IssueId;
        public IssueSeverity Severity;
        public string Title;
        public string Description;
        public double X, Y, Z;
        public double EdgeLengthMm;
        public double WedgeAngleDeg;
        public double NarrowFaceDimMm;
        public List<int> FaceTags;
        public List<int> EdgeTags;
        public RepairActionType SuggestedAction;
        public string RepairInstruction;
        public bool IsResolved;
        public string ResolvedNote;
    }

    public class ReviewReport
    {
        public string ReportId;
        public DateTime ReviewTime;
        public string PartFileName;
        public string PartFullPath;
        public string OverallStatus;
        public int TotalIssues;
        public int ErrorCount;
        public int WarnCount;
        public int ResolvedCount;
        public List<ReviewIssue> Issues;
        public string PartingSurfaceSummary;
        public List<string> UnavailableChecks;
        public string SourceSha256;
    }

    public class RepairResult
    {
        public string IssueId;
        public bool Success;
        public string ErrorMessage;
        public int FacesBefore;
        public int FacesAfter;
    }
}
