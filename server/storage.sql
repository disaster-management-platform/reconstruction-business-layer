-- Run through psql as the authorized storage administrator after provisioning a
-- private bucket reconstruction-attachments and a separate non-admin Auth identity.
-- Supply -v storage_identity=<verified technical auth.users UUID>.
-- No update, delete, public read or access to any other bucket is granted.
BEGIN;
CREATE POLICY reconstruction_attachment_select ON storage.objects FOR SELECT TO authenticated
 USING (bucket_id='reconstruction-attachments' AND (select auth.uid())=:'storage_identity'::uuid);
CREATE POLICY reconstruction_attachment_insert ON storage.objects FOR INSERT TO authenticated
 WITH CHECK (bucket_id='reconstruction-attachments' AND (select auth.uid())=:'storage_identity'::uuid
             AND name ~ '^[0-9a-f]{32}/[0-9a-f]{32}$');
COMMIT;
