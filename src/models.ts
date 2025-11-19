export interface VitallyResponse {
	results: any[];
	next?: string;
	atEnd?: boolean;
}

export interface VitallyAccount {
	id: string;
	name: string;
	createdAt: string;
	updatedAt: string;
	npsScore: number;
	externalId?: string;
	[key: string]: any;
}
