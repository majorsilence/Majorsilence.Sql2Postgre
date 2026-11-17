SELECT BusinessEntityID, OrganizationNode.ToString() AS node, OrganizationLevel, OrganizationNode.GetAncestor(1).ToString() AS manager
FROM HumanResources.Employee
ORDER BY OrganizationNode