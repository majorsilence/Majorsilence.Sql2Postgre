SELECT d.Title, d.DocumentNode.ToString() AS node, d.DocumentLevel, parent.Title AS parent
FROM Production.Document AS d
LEFT JOIN Production.Document AS parent ON parent.DocumentNode = d.DocumentNode.GetAncestor(1)
WHERE d.DocumentNode.IsDescendantOf(CAST('/1/' AS hierarchyid)) = 1
ORDER BY d.DocumentNode DESC