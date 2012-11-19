namespace EventStore
{
	using System;

	internal static class ExtensionMethods
	{
		public static bool IsValid(this Commit attempt)
		{
			if (attempt == null)
				throw new ArgumentNullException("attempt");

			if (!attempt.HasIdentifier())
				throw new ArgumentException(Resources.CommitsMustBeUniquelyIdentified, "attempt");

			if (attempt.CommitSequence <= 0)
				throw new ArgumentException(Resources.NonPositiveSequenceNumber, "attempt");

			if (attempt.StreamRevision <= 0)
				throw new ArgumentException(Resources.NonPositiveRevisionNumber, "attempt");

			if (attempt.StreamRevision < attempt.CommitSequence)
				throw new ArgumentException(Resources.RevisionTooSmall, "attempt");

			return true;
		}

		public static bool HasIdentifier(this Commit attempt)
		{
			return !string.IsNullOrEmpty(attempt.StreamId) && attempt.CommitId != Guid.Empty;
		}

		public static bool IsEmpty(this Commit attempt)
		{
			return attempt == null || attempt.Events.Count == 0;
		}

        public static bool IsEmpty(this string id)
        {
            return string.IsNullOrEmpty(id);
        }
        public static bool IsNotEmpty(this string id)
        {
            return !string.IsNullOrEmpty(id);
        }

	}
}